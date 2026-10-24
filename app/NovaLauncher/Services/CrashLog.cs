using System;
using System.IO;

namespace NovaLauncher.Services;

/// <summary>崩溃记录：把未处理的托管异常写入数据目录 logs/crash-*.log，供事后排查。</summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(string source, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var path = Path.Combine(AppPaths.Logs, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var body = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(path, body);
            }
        }
        catch
        {
            // 日志失败不能再引发异常
        }
    }
}
