using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NovaLauncher.PluginApi;

namespace NovaLauncher.PluginHosting;

/// <summary>
/// C 插件宿主：把 plugins/*.dylib | *.so | *.dll 按 nova_plugin.h ABI 加载为原生插件。
/// 插件导出 nova_plugin_init(const nova_plugin_api*)/nova_plugin_action/nova_plugin_unload，
/// 通过 api 结构体回调使用启动器能力（日志/通知/设置/页面/卡片/事件）。
/// </summary>
public sealed class CPluginHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly List<Delegate> _keepAlive = new();
    private readonly List<(string EventName, IntPtr Cb, IntPtr User)> _subscriptions = new();

    private IntPtr _handle;
    private ILauncherApi? _api;
    private IntPtr _launcherVersionPtr;
    private IntPtr _apiStructPtr;
    private bool _disposed;

    public CPluginHost(string libPath) => LibPath = Path.GetFullPath(libPath);

    public string LibPath { get; }
    public string Name { get; private set; } = "";
    public string Version { get; private set; } = "1.0.0";
    public string Author { get; private set; } = "unknown";
    public string Description { get; private set; } = "";
    public string? StartupError { get; private set; }
    public bool IsLoaded { get; private set; }

    /// <summary>按 ABI 加载并调用 nova_plugin_init。返回是否成功。</summary>
    public bool Load(ILauncherApi api)
    {
        _api = api;
        try
        {
            _handle = NativeLibrary.Load(LibPath);
        }
        catch (Exception e)
        {
            StartupError = "加载动态库失败: " + e.Message;
            return false;
        }

        try
        {
            ReadMetadata();
            api.SetPluginName(Name);
            BuildApiStruct();
            var init = GetExport<PluginInitFn>("nova_plugin_init");
            if (init == null)
            {
                StartupError = "插件未导出 nova_plugin_init";
                return false;
            }
            if (init(_apiStructPtr) != 0)
            {
                StartupError = "nova_plugin_init 返回非 0";
                return false;
            }
            IsLoaded = true;
            PluginEventHub.Raised += OnHubEvent;
            return true;
        }
        catch (Exception e)
        {
            StartupError = e.Message;
            return false;
        }
    }

    /// <summary>调用插件 nova_plugin_action(actionId)。返回 null 表示成功（含未实现动作），否则为错误信息。</summary>
    public string? InvokeAction(string actionId)
    {
        if (!IsLoaded) return "插件未加载";
        try
        {
            var fn = GetExport<PluginActionFn>("nova_plugin_action");
            if (fn == null) return null;
            fn(actionId);
            return null;
        }
        catch (Exception e)
        {
            return "C 插件动作执行失败: " + e.Message;
        }
    }

    /// <summary>调用插件 nova_plugin_unload（可选导出，失败不抛出）。</summary>
    public void Unload()
    {
        if (!IsLoaded) return;
        try { GetExport<PluginUnloadFn>("nova_plugin_unload")?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PluginEventHub.Raised -= OnHubEvent;
        try { Unload(); } catch { }
        if (_handle != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
        if (_apiStructPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_apiStructPtr);
            _apiStructPtr = IntPtr.Zero;
        }
        if (_launcherVersionPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_launcherVersionPtr);
            _launcherVersionPtr = IntPtr.Zero;
        }
    }

    // ---------- 元数据 ----------

    private void ReadMetadata()
    {
        var info = GetExport<PluginInfoFn>("nova_plugin_meta");
        if (info != null)
        {
            try
            {
                var ptr = info();
                if (ptr != IntPtr.Zero)
                {
                    var ni = Marshal.PtrToStructure<NativePluginInfo>(ptr);
                    Name = ReadUtf8(ni.Name) ?? "";
                    Version = ReadUtf8(ni.Version) ?? "1.0.0";
                    Author = ReadUtf8(ni.Author) ?? "unknown";
                    Description = ReadUtf8(ni.Description) ?? "";
                    return;
                }
            }
            catch { /* 回退到导出函数 */ }
        }
        Name = ReadUtf8(GetExport<NameFn>("nova_plugin_name")?.Invoke() ?? IntPtr.Zero)
               ?? Path.GetFileNameWithoutExtension(LibPath);
        Version = ReadUtf8(GetExport<NameFn>("nova_plugin_version")?.Invoke() ?? IntPtr.Zero) ?? "1.0.0";
        Author = ReadUtf8(GetExport<NameFn>("nova_plugin_author")?.Invoke() ?? IntPtr.Zero) ?? "unknown";
        Description = ReadUtf8(GetExport<NameFn>("nova_plugin_description")?.Invoke() ?? IntPtr.Zero) ?? "";
    }

    private static string? ReadUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    // ---------- API 结构体 ----------

    private void BuildApiStruct()
    {
        _launcherVersionPtr = Marshal.StringToCoTaskMemUTF8(LauncherVersion);
        var s = new NativeApi
        {
            ApiVersion = 1,
            LauncherVersion = _launcherVersionPtr,
            Log = Keep<LogCb>(OnLog),
            Notify = Keep<NotifyCb>(OnNotify),
            GetSetting = Keep<GetSettingCb>(OnGetSetting),
            SetSetting = Keep<SetSettingCb>(OnSetSetting),
            Path = Keep<PathCb>(OnPath),
            RegisterCard = Keep<RegisterCardCb>(OnRegisterCard),
            RegisterPage = Keep<RegisterPageCb>(OnRegisterPage),
            UpdatePage = Keep<UpdatePageCb>(OnUpdatePage),
            UpdateCard = Keep<UpdateCardCb>(OnUpdateCard),
            SubscribeEvent = Keep<SubscribeEventCb>(OnSubscribeEvent),
        };
        _apiStructPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeApi>());
        Marshal.StructureToPtr(s, _apiStructPtr, false);
    }

    private IntPtr Keep<T>(T d) where T : Delegate
    {
        _keepAlive.Add(d);
        return Marshal.GetFunctionPointerForDelegate(d);
    }

    private static string LauncherVersion
        => typeof(CPluginHost).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    // ---------- 回调：插件 -> 启动器 ----------

    private void OnLog(string plugin, string message) => _api?.Log(message);

    private void OnNotify(string plugin, string title, string message) => _api?.Notify(title, message);

    private int OnGetSetting(string plugin, string key, string defaultValue, IntPtr outBuf, int outSize)
    {
        try
        {
            var v = _api?.GetSetting(key, defaultValue) ?? defaultValue;
            return CopyUtf8(v, outBuf, outSize);
        }
        catch { return 0; }
    }

    private void OnSetSetting(string plugin, string key, string value) => _api?.SetSetting(key, value);

    private int OnPath(string plugin, int which, IntPtr outBuf, int outSize)
    {
        try
        {
            var v = which == 0 ? _api?.LauncherHome : _api?.GameRoot;
            return CopyUtf8(v ?? "", outBuf, outSize);
        }
        catch { return 0; }
    }

    private void OnRegisterCard(string plugin, string cardJson)
    {
        try
        {
            var card = JsonSerializer.Deserialize<HomeCardInfo>(cardJson, JsonOpts);
            if (card != null) _api?.RegisterHomeCard(card);
        }
        catch { }
    }

    private void OnRegisterPage(string plugin, string pageJson)
    {
        try
        {
            var page = JsonSerializer.Deserialize<PluginPageInfo>(pageJson, JsonOpts);
            if (page != null) _api?.RegisterPage(page);
        }
        catch { }
    }

    private void OnUpdatePage(string plugin, string key, string body) => _api?.UpdatePage(key, body);

    private void OnUpdateCard(string plugin, string cardId, string body) => _api?.UpdateHomeCard(cardId, body);

    private void OnSubscribeEvent(string plugin, string eventName, IntPtr cb, IntPtr user)
    {
        lock (_subscriptions) _subscriptions.Add((eventName, cb, user));
    }

    private static int CopyUtf8(string value, IntPtr buf, int size)
    {
        if (buf == IntPtr.Zero || size <= 0) return 0;
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        var n = Math.Min(bytes.Length, size - 1);
        if (n > 0) Marshal.Copy(bytes, 0, buf, n);
        Marshal.WriteByte(buf, n, 0);
        return 1;
    }

    // ---------- 事件：启动器 -> 插件 ----------

    private void OnHubEvent(string eventName, string dataJson)
    {
        List<(string, IntPtr, IntPtr)> subs;
        lock (_subscriptions)
            subs = _subscriptions.Where(s => s.EventName == "*" || s.EventName == eventName).ToList();
        foreach (var (_, cb, user) in subs)
        {
            if (cb == IntPtr.Zero) continue;
            var namePtr = Marshal.StringToCoTaskMemUTF8(eventName);
            var dataPtr = Marshal.StringToCoTaskMemUTF8(dataJson);
            try
            {
                var d = Marshal.GetDelegateForFunctionPointer<EventCb>(cb);
                d(namePtr, dataPtr, user);
            }
            catch { }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
                Marshal.FreeCoTaskMem(dataPtr);
            }
        }
    }

    // ---------- 导出符号 ----------

    private T? GetExport<T>(string name) where T : Delegate
    {
        if (_handle == IntPtr.Zero) return null;
        try
        {
            var p = NativeLibrary.GetExport(_handle, name);
            if (p == IntPtr.Zero) return null;
            var d = Marshal.GetDelegateForFunctionPointer<T>(p);
            _keepAlive.Add(d);
            return d;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 原生签名 ----------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr PluginInfoFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NameFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PluginInitFn(IntPtr apiPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PluginActionFn([MarshalAs(UnmanagedType.LPUTF8Str)] string actionId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PluginUnloadFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotifyCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultValue, IntPtr outBuf, int outSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSettingCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PathCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin, int which,
        IntPtr outBuf, int outSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RegisterCardCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cardJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RegisterPageCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pageJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdatePageCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateCardCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cardId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SubscribeEventCb([MarshalAs(UnmanagedType.LPUTF8Str)] string plugin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string eventName, IntPtr cb, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EventCb(IntPtr eventNamePtr, IntPtr dataJsonPtr, IntPtr user);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeApi
    {
        public int ApiVersion;
        public IntPtr LauncherVersion;
        public IntPtr Log;
        public IntPtr Notify;
        public IntPtr GetSetting;
        public IntPtr SetSetting;
        public IntPtr Path;
        public IntPtr RegisterCard;
        public IntPtr RegisterPage;
        public IntPtr UpdatePage;
        public IntPtr UpdateCard;
        public IntPtr SubscribeEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePluginInfo
    {
        public IntPtr Name;
        public IntPtr Version;
        public IntPtr Author;
        public IntPtr Description;
    }
}
