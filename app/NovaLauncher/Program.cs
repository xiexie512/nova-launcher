using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using NovaLauncher.Services;

namespace NovaLauncher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 全局异常兜底：未处理的托管异常不再直接杀死进程，而是记录崩溃日志（logs/crash-*.log）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Task", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                // 中文字体回退
                DefaultFamilyName = "PingFang SC, Microsoft YaHei UI, Segoe UI, Inter",
            })
            .LogToTrace();
}
