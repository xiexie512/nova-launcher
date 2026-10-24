using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaLauncher.PluginApi;

namespace NovaLauncher.PluginHosting;

/// <summary>
/// Python 插件宿主：把 plugins/*.py 当作独立插件加载。
/// 通过子进程 python3 -u host.py plugin.py + JSON 行协议桥接，
/// 支持生命周期（on_load/on_unload）、动作（on_action）、事件（on_event）与完整 API。
/// </summary>
public sealed class PythonPluginHost : IDisposable
{
    private const string BridgeDir = "_nova_bridge";
    private const string BridgeFile = "host.py";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<(bool ok, string? error)>> _pending = new();
    private readonly StringBuilder _stderrTail = new();
    private long _seq;

    private Process? _proc;
    private Task? _readerTask;
    private Task? _stderrTask;
    private TaskCompletionSource<bool>? _hello;
    private bool _helloOk;
    private string? _helloError;
    private ILauncherApi? _api;
    private bool _disposed;

    public PythonPluginHost(string pluginPath) => PluginPath = Path.GetFullPath(pluginPath);

    public string PluginPath { get; }
    public string Name { get; private set; } = "";
    public string Version { get; private set; } = "1.0.0";
    public string Author { get; private set; } = "unknown";
    public string Description { get; private set; } = "";
    public string PythonPath { get; private set; } = "";
    public string? StartupError { get; private set; }
    public bool IsRunning
    {
        get
        {
            try { return _proc is { HasExited: false }; }
            catch { return false; }
        }
    }
    public string StderrTail { get { lock (_stderrTail) return _stderrTail.ToString(); } }

    /// <summary>解释器候选：环境变量 NOVA_PYTHON 优先，其次常见路径。</summary>
    private static readonly string[] PythonCandidates =
    {
        "python3", "/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3",
        "python", "/usr/bin/python", "py",
    };

    private static string? ResolvePython()
    {
        var fromEnv = Environment.GetEnvironmentVariable("NOVA_PYTHON");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        foreach (var c in PythonCandidates)
        {
            try
            {
                var psi = new ProcessStartInfo(c, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(3000);
                if (p.ExitCode == 0) return c;
            }
            catch { /* 该候选不可用，尝试下一个 */ }
        }
        return null;
    }

    /// <summary>把内嵌的 host.py 写出到 LauncherHome/_nova_bridge/，返回路径。环境变量 NOVA_PY_HOST 可覆盖。</summary>
    public static string ExtractBridgeHost(string launcherHome)
    {
        var fromEnv = Environment.GetEnvironmentVariable("NOVA_PY_HOST");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var dir = Path.Combine(launcherHome, BridgeDir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, BridgeFile);
        if (!File.Exists(target))
        {
            using var s = typeof(PythonPluginHost).Assembly.GetManifestResourceStream("NovaLauncher.PluginHosting.host.py")
                ?? throw new InvalidOperationException("内置 host.py 资源缺失");
            using var fs = File.Create(target);
            s.CopyTo(fs);
        }
        return target;
    }

    /// <summary>启动插件进程、等待元数据并执行 on_load。返回是否加载成功。</summary>
    public bool Start(ILauncherApi api, int timeoutMs = 60000)
    {
        _api = api;
        _hello = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var python = ResolvePython();
        if (python == null)
        {
            StartupError = "未找到 python3 解释器（可用环境变量 NOVA_PYTHON 指定）";
            return false;
        }
        PythonPath = python;
        var hostScript = ExtractBridgeHost(api.LauncherHome);

        try
        {
            var psi = new ProcessStartInfo(python, $"-u \"{hostScript}\" \"{PluginPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = Process.Start(psi);
            if (_proc == null)
            {
                StartupError = "无法启动 python3 进程";
                return false;
            }
        }
        catch (Exception e)
        {
            StartupError = "启动 python3 失败: " + e.Message;
            return false;
        }

        _readerTask = Task.Run(ReaderLoop);
        _stderrTask = Task.Run(StderrLoop);

        if (!_hello.Task.Wait(timeoutMs) || !_helloOk)
        {
            StartupError = _helloError ?? "插件进程未在超时时间内上报元数据";
            Kill();
            return false;
        }

        api.SetPluginName(Name);
        PluginEventHub.Raised += OnHubEvent;

        var (ok, err) = Call("on_load", Array.Empty<object>(), timeoutMs);
        if (!ok)
        {
            StartupError = "on_load 失败: " + err;
            PluginEventHub.Raised -= OnHubEvent;
            Kill();
            return false;
        }
        return true;
    }

    /// <summary>调用插件 on_action(actionId)。返回 null 表示成功，否则为错误信息。</summary>
    public string? InvokeAction(string actionId, int timeoutMs = 30000)
    {
        if (!IsRunning) return "插件进程未运行";
        var (ok, err) = Call("on_action", new object[] { actionId }, timeoutMs);
        return ok ? null : err;
    }

    /// <summary>调用插件 on_unload（失败不抛出）。</summary>
    public void Unload(int timeoutMs = 30000)
    {
        if (IsRunning) Call("on_unload", Array.Empty<object>(), timeoutMs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Unload(); } catch { }
        PluginEventHub.Raised -= OnHubEvent;
        Kill();
        try { _proc?.Dispose(); } catch { }
        try { _readerTask?.Wait(2000); } catch { }
    }

    // ---------- 桥接 ----------

    private void OnHubEvent(string eventName, string dataJson)
    {
        // dataJson 已是合法 JSON，直接内嵌，保证 Python 侧解析为 dict
        var line = "{\"type\":\"event\",\"event\":" + JsonSerializer.Serialize(eventName)
                   + ",\"data\":" + dataJson + "}";
        TryWriteRaw(line);
    }

    private (bool ok, string? error) Call(string method, object[] args, int timeoutMs)
    {
        var id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (!TryWrite(new { id, type = "call", method, args }))
        {
            _pending.TryRemove(id, out _);
            return (false, "桥接进程已退出，无法调用 " + method);
        }
        try
        {
            var (ok, err) = tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
            return (ok, err);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            return (false, $"Python 插件调用 {method} 超时（{timeoutMs}ms）");
        }
    }

    private void ReaderLoop()
    {
        try
        {
            var stdout = _proc!.StandardOutput;
            while (!stdout.EndOfStream)
            {
                var line = stdout.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t))
                    {
                        switch (t.GetString())
                        {
                            case "hello":
                                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                                {
                                    Name = Str(root, "name") ?? "";
                                    Version = Str(root, "version") ?? "1.0.0";
                                    Author = Str(root, "author") ?? "unknown";
                                    Description = Str(root, "description") ?? "";
                                    _helloOk = true;
                                }
                                else _helloError = Str(root, "error") ?? "插件上报元数据失败";
                                _hello?.TrySetResult(true);
                                continue;
                            case "api":
                                HandleApiRequest(root);
                                continue;
                        }
                    }
                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
                        && _pending.TryGetValue(id, out var tcs))
                    {
                        var ok2 = !root.TryGetProperty("ok", out var okProp) || okProp.GetBoolean();
                        var err2 = Str(root, "error");
                        _pending.TryRemove(id, out _);
                        tcs.TrySetResult((ok2, err2));
                    }
                }
                catch { /* 单行解析失败不影响后续 */ }
            }
        }
        catch { }
        finally
        {
            _hello?.TrySetResult(true); // 进程退出时若还在等 hello，唤醒并判失败
            FailAllPending("插件进程已退出");
        }
    }

    private void StderrLoop()
    {
        try
        {
            var err = _proc!.StandardError;
            while (!err.EndOfStream)
            {
                var line = err.ReadLine();
                if (line == null) continue;
                lock (_stderrTail)
                {
                    if (_stderrTail.Length > 8000) _stderrTail.Clear();
                    _stderrTail.AppendLine(line);
                }
            }
        }
        catch { }
    }

    private void HandleApiRequest(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var v) ? v : 0;
        var method = Str(root, "method") ?? "";
        var args = root.TryGetProperty("args", out var a) ? a : default;
        try
        {
            var result = DispatchApi(method, args);
            WriteResponse(id, true, result);
        }
        catch (Exception e)
        {
            WriteResponse(id, false, e.Message);
        }
    }

    private object? DispatchApi(string method, JsonElement args)
    {
        var api = _api ?? throw new InvalidOperationException("api 未初始化");
        switch (method)
        {
            case "log":
                api.Log(ArgString(args, 0));
                return null;
            case "notify":
                api.Notify(ArgString(args, 0), ArgString(args, 1));
                return null;
            case "get_setting":
                return api.GetSetting(ArgString(args, 0), ArgString(args, 1, ""));
            case "set_setting":
                api.SetSetting(ArgString(args, 0), ArgString(args, 1));
                return null;
            case "launcher_home":
                return api.LauncherHome;
            case "game_root":
                return api.GameRoot;
            case "register_home_card":
                api.RegisterHomeCard(Deserialize<HomeCardInfo>(ArgJson(args, 0)));
                return null;
            case "register_page":
                api.RegisterPage(Deserialize<PluginPageInfo>(ArgJson(args, 0)));
                return null;
            case "update_page":
                api.UpdatePage(ArgString(args, 0), ArgString(args, 1));
                return null;
            case "update_home_card":
                api.UpdateHomeCard(ArgString(args, 0), ArgString(args, 1));
                return null;
            default:
                throw new InvalidOperationException("未知 API: " + method);
        }
    }

    private static T Deserialize<T>(JsonElement e) where T : class
        => JsonSerializer.Deserialize<T>(e.GetRawText(), JsonOpts) ?? throw new InvalidOperationException($"参数无法解析为 {typeof(T).Name}");

    private static string ArgString(JsonElement args, int index, string fallback = "")
    {
        if (args.ValueKind != JsonValueKind.Array) return fallback;
        var arr = args.GetArrayLength();
        if (index >= arr) return fallback;
        return args[index].ValueKind == JsonValueKind.String ? args[index].GetString() ?? fallback : fallback;
    }

    private static JsonElement ArgJson(JsonElement args, int index)
    {
        if (args.ValueKind == JsonValueKind.Array && index < args.GetArrayLength())
            return args[index];
        return default;
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private void WriteResponse(long id, bool ok, object? result)
    {
        TryWrite(ok
            ? new { id, ok, result }
            : new { id, ok, error = result?.ToString() ?? "unknown error" });
    }

    private bool TryWrite(object payload)
    {
        try
        {
            return TryWriteRaw(JsonSerializer.Serialize(payload));
        }
        catch
        {
            return false;
        }
    }

    private bool TryWriteRaw(string line)
    {
        lock (_writeLock)
        {
            if (_proc == null || _proc.HasExited) return false;
            try
            {
                _proc.StandardInput.Write(line);
                _proc.StandardInput.Write('\n');
                _proc.StandardInput.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void Kill()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(true);
        }
        catch { }
        FailAllPending("插件进程已退出");
    }

    private void FailAllPending(string why)
    {
        foreach (var kv in _pending.ToArray())
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetResult((false, why));
    }
}
