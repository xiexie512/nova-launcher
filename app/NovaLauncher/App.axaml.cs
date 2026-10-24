using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NovaLauncher.Services;
using NovaLauncher.ViewModels;
using NovaLauncher.Views;

namespace NovaLauncher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // UI 线程未处理异常兜底：记录日志并阻止进程退出（Avalonia 默认会让进程直接崩溃）
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write("Dispatcher", e.Exception);
            e.Handled = true;
        };

        AppServices.Init();
        ApplyTheme();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>按设置应用主题与强调色；设置页切换后即时调用。</summary>
    public static void ApplyTheme()
    {
        var s = SettingsService.S;
        var app = Current;
        if (app == null) return;
        app.RequestedThemeVariant = s.Theme switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default, // 跟随系统
        };
        if (Color.TryParse(s.AccentColor, out var c))
            app.Resources["AccentBrush"] = new SolidColorBrush(c);
    }
}
