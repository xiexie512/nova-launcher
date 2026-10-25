using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.PluginApi;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public enum PageKey { Home, Versions, Accounts, Modpacks, Plugins, Monitor, Lan, Labs, Settings, Console }

public sealed partial class MainViewModel : ObservableObject
{
    private PageKey _page = PageKey.Home;
    private string? _pluginPageKey;

    public HomeViewModel Home { get; } = new();
    public VersionsViewModel Versions { get; }
    public AccountsViewModel Accounts { get; } = new();
    public ModpacksViewModel Modpacks { get; }
    public PluginsViewModel Plugins { get; } = new();
    public MonitorViewModel Monitor { get; } = new();
    public LanViewModel Lan { get; } = new();
    public LabsViewModel Labs { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    /// <summary>插件注册的动态页面。</summary>
    public ObservableCollection<PluginPageViewModel> PluginPages { get; } = new();

    private bool _downloading;
    private string _downloadText = "";
    private bool _gameRunning;

    public object Current { get; private set; }

    public MainViewModel()
    {
        Versions = new VersionsViewModel(AppServices.Versions);
        Modpacks = new ModpacksViewModel();
        PluginService.RegisteredPages.CollectionChanged += (_, _) => SyncPluginPages();
        SyncPluginPages();
        Current = Home;
        Home.NavigateRequested += p => Page = p;
        // 编辑主页模式（CustomizeOpen）切换时，同步更新导航栏可见性
        Home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.CustomizeOpen)) OnPropertyChanged(nameof(NavVisible));
        };
        AppServices.Versions.Progress += (phase, done, total, finished) =>
        {
            Downloading = !finished;
            DownloadText = finished ? "" : $"{PhaseName(phase)} {done}/{total}";
        };
        AppServices.Launcher.Stopped += () => GameRunning = false;
    }

    private void SyncPluginPages()
    {
        PluginPages.Clear();
        foreach (var p in PluginService.RegisteredPages)
            PluginPages.Add(new PluginPageViewModel(p));
        OnPropertyChanged(nameof(HasPluginPages));
        if (PluginPageKey != null && !PluginPages.Any(p => p.Key == PluginPageKey))
            PluginPageKey = null;
    }

    private static string PhaseName(string p) => p switch { "A" => "客户端与库", "B" => "资源文件", _ => p };

    public PageKey Page
    {
        get => _page;
        set
        {
            SetProperty(ref _page, value);
            PluginPageKey = null;
            NotifyNav();
        }
    }

    /// <summary>当前插件页 key；null = 固定页。</summary>
    public string? PluginPageKey
    {
        get => _pluginPageKey;
        set
        {
            if (SetProperty(ref _pluginPageKey, value))
            {
                UpdatePluginActive();
                UpdateCurrent();
                OnPropertyChanged(nameof(IsHome)); OnPropertyChanged(nameof(IsVersions));
                OnPropertyChanged(nameof(IsAccounts)); OnPropertyChanged(nameof(IsModpacks));
                OnPropertyChanged(nameof(IsPlugins)); OnPropertyChanged(nameof(IsMonitor));
                OnPropertyChanged(nameof(IsLan)); OnPropertyChanged(nameof(IsLabs));
                OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsConsole));
                OnPropertyChanged(nameof(NavVisible));
            }
        }
    }

    public bool HasPluginPages => PluginPages.Count > 0;

    private void NotifyNav()
    {
        UpdatePluginActive();
        UpdateCurrent();
        OnPropertyChanged(nameof(IsHome)); OnPropertyChanged(nameof(IsVersions));
        OnPropertyChanged(nameof(IsAccounts)); OnPropertyChanged(nameof(IsModpacks));
        OnPropertyChanged(nameof(IsPlugins)); OnPropertyChanged(nameof(IsMonitor));
        OnPropertyChanged(nameof(IsLan)); OnPropertyChanged(nameof(IsLabs));
        OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsConsole));
        OnPropertyChanged(nameof(NavVisible));
    }

    private void UpdateCurrent()
    {
        if (PluginPageKey != null)
        {
            Current = PluginPages.FirstOrDefault(p => p.Key == PluginPageKey) ?? (object)Home;
        }
        else
        {
            Current = _page switch
            {
                PageKey.Home => Home,
                PageKey.Versions => Versions,
                PageKey.Accounts => Accounts,
                PageKey.Modpacks => Modpacks,
                PageKey.Plugins => Plugins,
                PageKey.Monitor => Monitor,
                PageKey.Lan => Lan,
                PageKey.Labs => Labs,
                PageKey.Settings => Settings,
                _ => Console,
            };
        }
        OnPropertyChanged(nameof(Current));
    }

    private void UpdatePluginActive()
    {
        foreach (var p in PluginPages) p.IsActive = p.Key == PluginPageKey;
    }

    public bool IsHome => PluginPageKey == null && Page == PageKey.Home;
    public bool IsVersions => PluginPageKey == null && Page == PageKey.Versions;
    public bool IsAccounts => PluginPageKey == null && Page == PageKey.Accounts;
    public bool IsModpacks => PluginPageKey == null && Page == PageKey.Modpacks;
    public bool IsPlugins => PluginPageKey == null && Page == PageKey.Plugins;
    public bool IsMonitor => PluginPageKey == null && Page == PageKey.Monitor;
    public bool IsLan => PluginPageKey == null && Page == PageKey.Lan;
    public bool IsLabs => PluginPageKey == null && Page == PageKey.Labs;
    public bool IsSettings => PluginPageKey == null && Page == PageKey.Settings;
    public bool IsConsole => PluginPageKey == null && Page == PageKey.Console;

    /// <summary>
    /// 导航栏可见性：仅在首页且开启编辑主页模式（CustomizeOpen）时隐藏导航栏；
    /// 其余页面或未进入编辑模式时始终显示。
    /// </summary>
    public bool NavVisible => !IsHome || !Home.CustomizeOpen;

    public bool Downloading { get => _downloading; set => SetProperty(ref _downloading, value); }
    public string DownloadText { get => _downloadText; set => SetProperty(ref _downloadText, value); }
    public bool GameRunning { get => _gameRunning; set => SetProperty(ref _gameRunning, value); }

    public string KernelVersion => AppServices.KernelVersion;
    public string HomeDir => AppPaths.Home;

    [RelayCommand]
    private void Go(PageKey p) => Page = p;

    [RelayCommand]
    private void GoPlugin(string key) => PluginPageKey = key;

    [RelayCommand]
    private void StopGame() => AppServices.Launcher.Stop();
}
