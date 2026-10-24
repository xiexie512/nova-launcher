using System.Diagnostics;
using System.Text;

namespace PyCExample;

/// <summary>
/// 插件侧对本机 Python3 的调用封装：定位解释器、执行脚本、捕获输出（UTF-8）。
/// 与 C 集成不同，Python 走进程调用（python3 script.py ...），脚本放在
/// LauncherHome/PyCExample/scripts/ 下，可直接编辑，无需重新编译插件。
/// </summary>
internal static class PyRunner
{
    private static string? _python;
    private static string? _pythonVersion;

    /// <summary>候选解释器：优先 PATH 上的 python3，其次常见绝对路径（含 Windows）。</summary>
    private static readonly string[] Candidates =
    {
        "python3", "/usr/bin/python3", "/usr/local/bin/python3",
        "python", "/usr/bin/python", "py",
    };

    public static string PythonPath
    {
        get
        {
            _python ??= FindPython();
            return _python ?? "(未找到 python3)";
        }
    }

    public static string PythonVersion
    {
        get
        {
            if (_pythonVersion is null)
            {
                var py = _python ?? FindPython();
                _pythonVersion = py is null ? "?" : (RunRaw(py, "--version") ?? "?").Trim();
            }
            return string.IsNullOrWhiteSpace(_pythonVersion) ? "?" : _pythonVersion;
        }
    }

    private static string? FindPython()
    {
        foreach (var c in Candidates)
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

    /// <summary>执行 Python 脚本（args 为脚本参数，自动加引号），返回 stdout+stderr，超时 30 秒。</summary>
    public static string RunScript(string scriptPath, string args)
    {
        var py = PythonPath;
        if (py.StartsWith("(未找到")) return "Python3 不可用：未找到解释器";
        return RunRaw(py, $"\"{scriptPath}\" {args}") ?? "(脚本无输出)";
    }

    private static string? RunRaw(string python, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(python, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(true); } catch { }
                proc.WaitForExit();
                return "Python 脚本执行超时（30 秒）";
            }
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var text = stdout + stderr;
            return string.IsNullOrWhiteSpace(text) ? "(脚本无输出)" : text.TrimEnd();
        }
        catch (Exception e)
        {
            return "Python 调用失败: " + e.Message;
        }
    }
}
