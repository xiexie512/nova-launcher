using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.PluginApi;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private bool _busy;
    private string _status = "";
    private bool _isRunning;
    private bool _customizeOpen;

    public HomeViewModel()
    {
        AppServices.Launcher.Stopped += () =>
        {
            IsRunning = false;
            RefreshHomeDisplay();
        };
        AppServices.Versions.Progress += (phase, done, total, finished) =>
        {
            if (!finished) DownloadStatus = $"{PhaseName(phase)} {done}/{total}";
        };
        PluginService.RegisteredHomeCards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPluginCards));
            OnPropertyChanged(nameof(ShowPluginCardsVisible));
        };
        RefreshData();
        RefreshHomeDisplay();
        _ = Task.Run(RefreshLanSync);
        _ = Task.Run(RefreshServerStatusAsync);
        _ = RefreshVersionsCommand.ExecuteAsync(null);
        InitWidgetGrid();
    }

    private static string PhaseName(string p) => p switch { "A" => "客户端与库", "B" => "资源文件", _ => p };

    // ============ 顶部问候（日期 + 文案） ============
    public string GreetingDate { get; private set; } = "";
    public string GreetingLine { get; private set; } = "";

    // ============ 日历 ============
    public List<List<CalendarDay>> CalendarWeeks { get; private set; } = new();
    public string CalendarMonth { get; private set; } = "";
    public string TodayPlayedText { get; private set; } = "这一天没有游玩记录。";

    // ============ 固定的世界 ============
    public ObservableCollection<SaveInfo> PinnedWorlds { get; } = new();

    // ============ 固定的实例 ============
    public ObservableCollection<PinnedInstance> PinnedInstances { get; } = new();

    // ============ 从最近的项目开始 ============
    public ObservableCollection<RecentProject> RecentProjects { get; } = new();

    // ---------- 快捷启动 ----------
    public ObservableCollection<VersionEntry> Instances { get; } = new();

    private VersionEntry? _selectedEntry;
    public VersionEntry? SelectedEntry
    {
        get => _selectedEntry;
        set { SetProperty(ref _selectedEntry, value); OnPropertyChanged(nameof(CanLaunch)); }
    }

    public bool CanLaunch => SelectedEntry != null && SettingsService.S.SelectedAccount != null && !_busy && !_isRunning;
    public bool CanStop => _isRunning;
    public string AccountName => SettingsService.S.SelectedAccount?.Name ?? "未选择账户";
    public string AccountType => SettingsService.S.SelectedAccount?.TypeLabel ?? "";
    public string RunningText => IsRunning ? "游戏运行中" : "没有运行中的实例";
    public bool Busy { get => _busy; set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanLaunch)); } }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsRunning { get => _isRunning; set { SetProperty(ref _isRunning, value); OnPropertyChanged(nameof(CanLaunch)); OnPropertyChanged(nameof(CanStop)); OnPropertyChanged(nameof(RunningText)); } }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        var acc = SettingsService.S.SelectedAccount;
        if (SelectedEntry == null || acc == null) { Status = "请先选择实例并添加账户"; return; }
        Busy = true;
        Status = "启动中…";
        try
        {
            var (ok, err) = await AppServices.Launcher.LaunchAsync(SelectedEntry, acc);
            Status = ok ? "游戏运行中" : "启动失败: " + (err ?? "未知错误");
            IsRunning = ok;
        }
        catch (Exception e) { Status = "启动失败: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void StopGame() => AppServices.Launcher.Stop();

    [RelayCommand]
    private async Task LaunchInstanceAsync(VersionEntry? v)
    {
        if (v == null) return;
        var acc = SettingsService.S.SelectedAccount;
        if (acc == null) { Status = "请先添加并选择账户"; return; }
        Busy = true;
        Status = "启动 " + v.Id + " …";
        try
        {
            var (ok, err) = await AppServices.Launcher.LaunchAsync(v, acc);
            Status = ok ? "游戏运行中" : "启动失败: " + (err ?? "未知错误");
            IsRunning = ok;
        }
        catch (Exception e) { Status = "启动失败: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void OpenInstance(VersionEntry? v)
    {
        if (v == null) return;
        try
        {
            var dir = Path.Combine(AppPaths.VersionDir(v.Id), "instance");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open") { Arguments = "\"" + dir + "\"", UseShellExecute = true });
        }
        catch { Status = "无法打开实例目录"; }
    }

    [RelayCommand]
    private void ToggleCustomize() => CustomizeOpen = !CustomizeOpen;

    // ---------- 主页区块可见性（自定义主页） ----------
    public bool CustomizeOpen { get => _customizeOpen; set => SetProperty(ref _customizeOpen, value); }

    public bool ShowInstances
    {
        get => SettingsService.S.HomeShowInstances;
        set { SettingsService.S.HomeShowInstances = value; OnPropertyChanged(); SettingsService.Save(); }
    }
    public bool ShowSaves
    {
        get => SettingsService.S.HomeShowSaves;
        set { SettingsService.S.HomeShowSaves = value; OnPropertyChanged(); SettingsService.Save(); }
    }
    public bool ShowServers
    {
        get => SettingsService.S.HomeShowServers;
        set { SettingsService.S.HomeShowServers = value; OnPropertyChanged(); SettingsService.Save(); }
    }
    public bool ShowLan
    {
        get => SettingsService.S.HomeShowLan;
        set { SettingsService.S.HomeShowLan = value; OnPropertyChanged(); SettingsService.Save(); }
    }
    public bool ShowPluginCards
    {
        get => SettingsService.S.HomeShowPluginCards;
        set { SettingsService.S.HomeShowPluginCards = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowPluginCardsVisible)); SettingsService.Save(); }
    }
    public bool HasPluginCards => PluginService.RegisteredHomeCards.Count > 0;
    public bool ShowPluginCardsVisible => ShowPluginCards && HasPluginCards;
    public ObservableCollection<HomeCardInfo> PluginCards => PluginService.RegisteredHomeCards;

    // ---------- 我的存档 ----------
    public ObservableCollection<SaveInfo> Saves { get; } = new();

    [RelayCommand]
    private void OpenSave(SaveInfo? s)
    {
        if (s == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open") { Arguments = "\"" + s.FullPath + "\"", UseShellExecute = true }); }
        catch { Status = "无法打开存档目录"; }
    }

    [RelayCommand]
    private void ExportSave(SaveInfo? s)
    {
        if (s == null) return;
        try
        {
            var outDir = Path.Combine(AppPaths.Exports, "saves");
            Directory.CreateDirectory(outDir);
            var zip = Path.Combine(outDir, $"saves-{s.Version}-{s.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(s.FullPath, zip);
            Status = "存档已导出: " + zip;
        }
        catch (Exception e) { Status = "导出失败: " + e.Message; }
    }

    [RelayCommand]
    private void DeleteSave(SaveInfo? s)
    {
        if (s == null) return;
        try
        {
            var trash = Path.Combine(AppPaths.Exports, "trash");
            Directory.CreateDirectory(trash);
            var dest = Path.Combine(trash, $"{s.Version}-{s.Name}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.Move(s.FullPath, dest);
            Status = "存档已移入回收夹: " + dest;
            RefreshSaves();
        }
        catch (Exception e) { Status = "删除失败: " + e.Message; }
    }

    [RelayCommand]
    private void PinWorld(SaveInfo? s)
    {
        if (s == null) return;
        var key = s.Version + "::" + s.Name;
        var pinned = SettingsService.S.PinnedWorlds;
        if (pinned.Contains(key)) pinned.Remove(key); else pinned.Add(key);
        SettingsService.Save();
        RefreshPinnedWorlds();
        Status = pinned.Contains(key) ? "已将世界固定到主页" : "已取消固定该世界";
    }

    public bool IsWorldPinned(SaveInfo s) => SettingsService.S.PinnedWorlds.Contains(s.Version + "::" + s.Name);

    // ---------- 我的服务器 ----------
    public ObservableCollection<ServerEntry> Servers { get; } = new();

    [RelayCommand]
    private void AddServer(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        var parts = arg.Split('|');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        { Status = "服务器名称与地址不能为空"; return; }
        SettingsService.S.Servers.Add(new ServerEntry { Name = parts[0], Address = parts[1], Version = parts.Length > 2 ? parts[2] : "" });
        SettingsService.Save();
        SyncServers();
        Status = "已添加服务器 " + parts[0];
    }

    [RelayCommand]
    private void RemoveServer(ServerEntry? s)
    {
        if (s == null) return;
        SettingsService.S.Servers.Remove(s);
        SettingsService.Save();
        SyncServers();
    }

    public void CopyServerAddress(ServerEntry s) => Status = $"已复制 {s.Address}，在游戏「多人游戏→添加服务器」粘贴即可连接";

    /// <summary>后台探测服务器在线人数（X/N 人在线）。</summary>
    private async Task RefreshServerStatusAsync()
    {
        while (true)
        {
            foreach (var s in Servers.ToList())
            {
                if (string.IsNullOrWhiteSpace(s.Address)) continue;
                var r = await ServerPingService.PingAsync(s.Address);
                if (r != null)
                {
                    s.Online = r.Value.Online;
                    s.Max = r.Value.Max;
                }
                else { s.Max = 0; }
                OnPropertyChanged(nameof(Servers));
            }
            await Task.Delay(15000);
        }
    }

    // ---------- 虚拟局域网（ZeroTier 集成） ----------
    private string _lanStatus = "未检测";
    private string _lanIp = "尚未分配";
    private bool _lanInstalled;

    public string LanStatus { get => _lanStatus; set => SetProperty(ref _lanStatus, value); }
    public string LanIp { get => _lanIp; set => SetProperty(ref _lanIp, value); }
    public bool LanInstalled { get => _lanInstalled; set { SetProperty(ref _lanInstalled, value); OnPropertyChanged(nameof(LanNotInstalled)); } }
    public bool LanNotInstalled => !LanInstalled;

    public event Action<PageKey>? NavigateRequested;

    [RelayCommand]
    private void GoLan() => NavigateRequested?.Invoke(PageKey.Lan);

    private void RefreshLanSync()
    {
        try
        {
            ZeroTierService.Detect();
            var installed = ZeroTierService.IsInstalled;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LanInstalled = installed;
                if (!installed)
                {
                    LanStatus = "未安装虚拟局域网引擎";
                    LanIp = "未安装";
                    LogService.Info("[虚拟局域网] 未检测到 ZeroTier，可在「虚拟局域网」页一键安装");
                    return;
                }
                try
                {
                    var info = ZeroTierService.GetInfo();
                    LanStatus = info.Online ? $"ZeroTier {info.Version} · 已连接" : "引擎离线";
                    var ip = ZeroTierService.MyVirtualIp();
                    LanIp = string.IsNullOrEmpty(ip) ? "尚未分配" : ip;
                    LogService.Info($"[虚拟局域网] {LanStatus} · 虚拟 IP {LanIp} · 节点 {info.NodeId}");
                }
                catch { LanStatus = "引擎读取失败"; }
            });
        }
        catch { }
    }

    // ---------- 版本下载（主页卡片） ----------
    public ObservableCollection<VersionEntry> DownloadVersions { get; } = new();

    private VersionEntry? _selectedDownload;
    public VersionEntry? SelectedDownloadVersion
    {
        get => _selectedDownload;
        set
        {
            SetProperty(ref _selectedDownload, value);
            OnPropertyChanged(nameof(CanDownloadVersion));
            OnPropertyChanged(nameof(CanLaunchDownloaded));
        }
    }

    private bool _downloadBusy;
    public bool DownloadBusy
    {
        get => _downloadBusy;
        set
        {
            SetProperty(ref _downloadBusy, value);
            OnPropertyChanged(nameof(CanDownloadVersion));
            OnPropertyChanged(nameof(CanLaunchDownloaded));
        }
    }

    private string _downloadStatus = "";
    public string DownloadStatus { get => _downloadStatus; set => SetProperty(ref _downloadStatus, value); }

    public bool CanDownloadVersion => SelectedDownloadVersion != null && !DownloadBusy && !IsRunning;
    public bool CanLaunchDownloaded => SelectedDownloadVersion is { Installed: true } && !DownloadBusy && !IsRunning;

    public bool ShowVersions
    {
        get => SettingsService.S.HomeShowVersions;
        set { SettingsService.S.HomeShowVersions = value; OnPropertyChanged(); SettingsService.Save(); }
    }

    /// <summary>当前生效的下载源（多源聚合配置）。</summary>
    public string DownloadSourceText
    {
        get
        {
            var mojang = SettingsService.S.UseMojang;
            var bm = SettingsService.S.UseBmclapi;
            return (mojang, bm) switch
            {
                (true, true) => "官方 + BMCLAPI 镜像",
                (true, false) => "仅 Mojang 官方",
                (false, true) => "仅 BMCLAPI 镜像",
                _ => "未启用任何下载源",
            };
        }
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        if (DownloadBusy) return;
        DownloadBusy = true;
        DownloadStatus = "正在获取版本清单…";
        try
        {
            var list = await AppServices.Versions.FetchManifestAsync();
            DownloadVersions.Clear();
            foreach (var v in list.OrderByDescending(x => x.ReleaseTime).Take(60))
                DownloadVersions.Add(v);
            if (SelectedDownloadVersion == null && DownloadVersions.Count > 0)
                SelectedDownloadVersion = DownloadVersions[0];
            DownloadStatus = $"已获取 {DownloadVersions.Count} 个版本（Mojang + BMCLAPI 多源）";
        }
        catch (Exception e)
        {
            DownloadStatus = "获取失败: " + e.Message;
        }
        finally
        {
            DownloadBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadVersionAsync()
    {
        var v = SelectedDownloadVersion;
        if (v == null || DownloadBusy) return;
        DownloadBusy = true;
        DownloadStatus = $"正在下载 {v.Id}（多源并行）…";
        try
        {
            var ok = await AppServices.Versions.InstallVersionAsync(v);
            if (ok)
            {
                v.Installed = true;
                DownloadStatus = $"「{v.Id}」下载完成，可点击启动";
                OnPropertyChanged(nameof(CanLaunchDownloaded));
                RefreshInstances();
            }
        }
        catch (Exception e)
        {
            DownloadStatus = "下载失败: " + e.Message;
            LogService.Error("版本下载失败: " + e);
        }
        finally
        {
            DownloadBusy = false;
        }
    }

    [RelayCommand]
    private void LaunchDownloaded()
    {
        if (SelectedDownloadVersion is { Installed: true })
            LaunchInstanceCommand.Execute(SelectedDownloadVersion);
    }

    [RelayCommand]
    private void OpenDownloadedDir()
    {
        if (SelectedDownloadVersion is { Installed: true })
            OpenInstanceCommand.Execute(SelectedDownloadVersion);
    }

    // ============ 主页展示数据（日历 / 问候 / 固定 / 最近项目） ============

    public void RefreshHomeDisplay()
    {
        var now = DateTime.Now;

        // 问候
        var wd = new[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" }[(int)now.DayOfWeek];
        GreetingDate = $"{now.Month}月{now.Day}日 {wd}";
        GreetingLine = now.Hour switch
        {
            >= 5 and < 11 => "早上好，轻松玩一局正合适。",
            >= 11 and < 14 => "中午好，趁休息来一局？",
            >= 14 and < 18 => "下午好，轻松玩一局正合适。",
            >= 18 and < 23 => "晚上好，来一局放松一下吧。",
            _ => "夜深了，注意休息，早点睡吧。",
        };
        OnPropertyChanged(nameof(GreetingDate));
        OnPropertyChanged(nameof(GreetingLine));

        // 日历
        BuildCalendar(now);
        var todayKey = now.ToString("yyyy-MM-dd");
        var todayCount = SettingsService.S.PlayLog.TryGetValue(todayKey, out var tc) ? tc : 0;
        TodayPlayedText = todayCount > 0 ? $"10月{now.Day}日你游玩了：{todayCount} 局" : $"10月{now.Day}日你游玩了：\n这一天没有游玩记录。";
        OnPropertyChanged(nameof(TodayPlayedText));

        RefreshPinnedWorlds();
        RefreshPinnedInstances();
        RefreshRecentProjects();
    }

    private void BuildCalendar(DateTime now)
    {
        CalendarMonth = $"{now.Year}年{now.Month}月";
        OnPropertyChanged(nameof(CalendarMonth));
        var first = new DateTime(now.Year, now.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        int leading = ((int)first.DayOfWeek + 6) % 7; // 周一=0
        var cells = new List<CalendarDay>();
        for (int i = 0; i < leading; i++)
            cells.Add(new CalendarDay { Day = 0, InMonth = false });
        for (int d = 1; d <= daysInMonth; d++)
        {
            var date = new DateTime(now.Year, now.Month, d);
            cells.Add(new CalendarDay
            {
                Day = d,
                InMonth = true,
                IsToday = date == now.Date,
                HasPlayed = SettingsService.S.PlayLog.ContainsKey(date.ToString("yyyy-MM-dd")),
            });
        }
        while (cells.Count % 7 != 0) cells.Add(new CalendarDay { Day = 0, InMonth = false });

        var weeks = new List<List<CalendarDay>>();
        for (int i = 0; i < cells.Count; i += 7) weeks.Add(cells.GetRange(i, 7));
        CalendarWeeks = weeks;
        OnPropertyChanged(nameof(CalendarWeeks));
    }

    private void RefreshPinnedWorlds()
    {
        PinnedWorlds.Clear();
        var pinned = SettingsService.S.PinnedWorlds;
        foreach (var s in Saves)
        {
            var key = s.Version + "::" + s.Name;
            if (pinned.Contains(key)) PinnedWorlds.Add(s);
        }
    }

    private void RefreshPinnedInstances()
    {
        PinnedInstances.Clear();
        var pinned = SettingsService.S.PinnedInstances;
        foreach (var id in pinned.ToList())
        {
            var inst = Instances.FirstOrDefault(x => x.Id == id);
            if (inst == null) continue;
            PinnedInstances.Add(new PinnedInstance
            {
                Id = inst.Id,
                Type = inst.Type,
                LastPlayedText = "游玩于 " + LastPlayedText(id),
                Entry = inst,
            });
        }
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        // 已安装实例按最近游玩排序
        var instances = Instances.ToList()
            .OrderByDescending(x => SettingsService.S.LastPlayedTimes.TryGetValue(x.Id, out var t) ? t : 0L);
        foreach (var inst in instances.Take(6))
            RecentProjects.Add(new RecentProject
            {
                Name = inst.Id,
                Tag = inst.Type,
                LastPlayedText = "上次游玩于 " + (SettingsService.S.LastPlayedTimes.TryGetValue(inst.Id, out var t2) ? LastPlayedText(inst.Id) : "未游玩"),
                IsServer = false,
                IsInstalled = true,
                Target = inst,
            });
        foreach (var s in Servers)
            RecentProjects.Add(new RecentProject
            {
                Name = s.Name,
                Tag = string.IsNullOrWhiteSpace(s.Version) ? "服务器" : s.Version,
                LastPlayedText = s.Address,
                IsServer = true,
                IsInstalled = true,
                Target = s,
            });
    }

    /// <summary>相对时间文案：今天 / 昨天 / X 天前 / X 周前 / 日期。</summary>
    public static string LastPlayedText(string id)
    {
        if (!SettingsService.S.LastPlayedTimes.TryGetValue(id, out var unix) || unix == 0) return "未游玩";
        var dt = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        var days = (DateTime.Now - dt).TotalDays;
        if (days < 1) return "今天";
        if (days < 2) return "昨天";
        if (days < 7) return $"{(int)days} 天前";
        if (days < 30) return $"{(int)(days / 7)} 周前";
        return dt.ToString("M月d日");
    }

    // ============ 最近项目 / 固定实例 的交互 ============

    [RelayCommand]
    private void LaunchRecent(RecentProject? p)
    {
        if (p == null) return;
        if (!p.IsServer && p.Target is VersionEntry v) LaunchInstanceCommand.Execute(v);
        else if (p.IsServer && p.Target is ServerEntry s) CopyServerAddress(s);
    }

    [RelayCommand]
    private void LaunchPinned(PinnedInstance? p)
    {
        if (p?.Entry != null) LaunchInstanceCommand.Execute(p.Entry);
    }

    [RelayCommand]
    private void TogglePinInstance(PinnedInstance? p)
    {
        if (p == null) return;
        var pinned = SettingsService.S.PinnedInstances;
        if (pinned.Contains(p.Id)) pinned.Remove(p.Id); else pinned.Add(p.Id);
        SettingsService.Save();
        RefreshPinnedInstances();
        RefreshRecentProjects();
        Status = pinned.Contains(p.Id) ? $"已固定实例 {p.Id}" : $"已取消固定 {p.Id}";
    }

    [RelayCommand]
    private void UnpinInstance(RecentProject? p)
    {
        if (p?.Target is VersionEntry v)
        {
            SettingsService.S.PinnedInstances.Remove(v.Id);
            SettingsService.Save();
            RefreshPinnedInstances();
            Status = $"已取消固定 {v.Id}";
        }
    }

    // ---------- 数据 ----------
    public void RefreshData()
    {
        RefreshInstances();
        RefreshSaves();
        SyncServers();
    }

    private void RefreshInstances()
    {
        Instances.Clear();
        if (!Directory.Exists(AppPaths.Versions)) return;
        foreach (var dir in Directory.GetDirectories(AppPaths.Versions))
        {
            var id = Path.GetFileName(dir);
            if (!VersionService.IsInstalled(id)) continue;
            Instances.Add(new VersionEntry
            {
                Id = id,
                Installed = true,
                Type = File.Exists(Path.Combine(dir, "pack.json")) ? "整合包" : "本地版本",
            });
        }
        if (_selectedEntry == null && Instances.Count > 0)
            SelectedEntry = Instances[0];
        OnPropertyChanged(nameof(CanLaunch));
    }

    private void RefreshSaves()
    {
        Saves.Clear();
        if (!Directory.Exists(AppPaths.Versions)) return;
        foreach (var vdir in Directory.GetDirectories(AppPaths.Versions))
        {
            var ver = Path.GetFileName(vdir);
            var saves = Path.Combine(vdir, "instance", "saves");
            if (!Directory.Exists(saves)) continue;
            foreach (var s in Directory.GetDirectories(saves))
            {
                try
                {
                    var name = Path.GetFileName(s);
                    var size = DirSize(s);
                    Saves.Add(new SaveInfo
                    {
                        Version = ver,
                        Name = name,
                        SizeBytes = size,
                        Modified = Directory.GetLastWriteTime(s),
                        SizeText = FormatSize(size),
                        FullPath = s,
                    });
                }
                catch { }
            }
        }
        RefreshPinnedWorlds();
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                total += new FileInfo(f).Length;
        }
        catch { }
        return total;
    }

    private static string FormatSize(long b) =>
        b >= 1 << 30 ? $"{b / 1073741824.0:F1} GB" :
        b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" :
        b >= 1 << 10 ? $"{b / 1024.0:F0} KB" : $"{b} B";

    private void SyncServers()
    {
        Servers.Clear();
        foreach (var s in SettingsService.S.Servers) Servers.Add(s);
        RefreshRecentProjects();
    }

    // ================= 首页插件网格 =================

    /// <summary>侧边栏可添加的插件 / 小组件目录（内置 + 插件卡片）。</summary>
    public ObservableCollection<WidgetCatalogItem> AvailableWidgets { get; } = new();
    /// <summary>已放置到网格的小组件。</summary>
    public ObservableCollection<PlacedWidget> HomeWidgets { get; } = new();

    private readonly Dictionary<(int, int), string> _cellOwner = new();
    private int _maxRows = 6;

    public double GridCanvasWidth => WidgetGrid.CanvasWidth;
    public double GridCanvasHeight { get; private set; } = 6 * (WidgetGrid.CellH + WidgetGrid.Gap);

    /// <summary>每行的实际高度（由卡片自然高度决定，至少一格高）。</summary>
    public double[] RowHeights { get; private set; } = Array.Empty<double>();

    /// <summary>第 row 行顶部的 Y 坐标（按动态行高累计）。</summary>
    public double RowTop(int row)
    {
        if (row <= 0 || RowHeights.Length == 0) return 0;
        double acc = 0;
        for (int i = 0; i < row && i < RowHeights.Length; i++) acc += RowHeights[i] + WidgetGrid.Gap;
        return acc;
    }

    /// <summary>由画布内 Y 坐标反查所在行（供拖拽定位用，行高不再均匀）。</summary>
    public int RowFromY(double top)
    {
        if (RowHeights.Length == 0) return 0;
        double acc = 0;
        for (int r = 0; r < RowHeights.Length; r++)
        {
            double span = RowHeights[r] + WidgetGrid.Gap;
            if (top < acc + span) return r;
            acc += span;
        }
        return RowHeights.Length - 1;
    }

    // ---- 时钟小组件 ----
    private Avalonia.Threading.DispatcherTimer? _clockTimer;
    public string ClockNow { get; private set; } = "";
    public string ClockDate { get; private set; } = "";

    // ---- 性能监控小组件 ----
    public double MonitorCpu { get; private set; }
    public double MonitorMem { get; private set; }
    public int MonitorThreads { get; private set; }
    public bool MonitorRunning { get; private set; }
    public string MonitorCpuText => MonitorRunning ? $"{MonitorCpu:F1}%" : "未运行";
    public string MonitorMemText => MonitorRunning ? $"{MonitorMem:F0} MB" : "—";
    public string MonitorThreadsText => MonitorRunning ? $"{MonitorThreads} 线程" : "—";
    public string MonitorRunningText => MonitorRunning ? "● 运行中" : "○ 未运行";

    private void InitWidgetGrid()
    {
        _clockTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            ClockNow = DateTime.Now.ToString("HH:mm:ss");
            ClockDate = DateTime.Now.ToString("M月d日 dddd");
            OnPropertyChanged(nameof(ClockNow));
            OnPropertyChanged(nameof(ClockDate));
        };
        _clockTimer.Start();

        MonitorService.Sample += s =>
        {
            MonitorRunning = s.Running;
            MonitorCpu = s.CpuPercent;
            MonitorMem = s.MemMb;
            MonitorThreads = s.Threads;
            OnPropertyChanged(nameof(MonitorRunning)); OnPropertyChanged(nameof(MonitorRunningText));
            OnPropertyChanged(nameof(MonitorCpu)); OnPropertyChanged(nameof(MonitorCpuText));
            OnPropertyChanged(nameof(MonitorMem)); OnPropertyChanged(nameof(MonitorMemText));
            OnPropertyChanged(nameof(MonitorThreads)); OnPropertyChanged(nameof(MonitorThreadsText));
        };

        PluginService.RegisteredHomeCards.CollectionChanged += (_, _) => BuildAvailableWidgets();
        BuildAvailableWidgets();
        LoadWidgetLayout();
    }

    private void BuildAvailableWidgets()
    {
        AvailableWidgets.Clear();
        foreach (var b in WidgetCatalog.BuiltIn) AvailableWidgets.Add(b);
        foreach (var c in PluginService.RegisteredHomeCards)
            AvailableWidgets.Add(new WidgetCatalogItem
            {
                TypeKey = "plugin_card",
                Title = c.Title,
                Icon = c.Icon,
                Description = c.Body,
                DefaultCols = 2,
                DefaultRows = 1,
                IsPluginCard = true,
                PluginCardId = c.Id,
            });
        OnPropertyChanged(nameof(AvailableWidgets));
    }

    /// <summary>从侧边栏添加一个小组件（自动寻位）。</summary>
    public void AddWidgetFromCatalog(WidgetCatalogItem item)
    {
        if (item == null) return;
        AddWidgetCore(item.TypeKey, item.DefaultCols, item.DefaultRows, item.IsPluginCard, item.PluginCardId, item.Title, item.Icon);
    }

    public void AddWidgetByType(string typeKey, string? pluginCardId)
    {
        var item = AvailableWidgets.FirstOrDefault(x => x.TypeKey == typeKey && x.PluginCardId == pluginCardId);
        if (item != null) AddWidgetFromCatalog(item);
    }

    private bool AddWidgetCore(string typeKey, int cols, int rows, bool isPluginCard, string? pluginCardId, string title, string icon)
    {
        cols = Math.Clamp(cols, 1, 2);
        rows = Math.Clamp(rows, 1, 2);
        var found = FindFree(cols, rows);
        if (found == null)
        {
            _maxRows += 2;
            found = FindFree(cols, rows);
        }
        if (found == null) return false;
        var w = new PlacedWidget
        {
            TypeKey = typeKey, Title = title, Icon = icon,
            Cols = cols, Rows = rows, Col = found.Value.col, Row = found.Value.row,
            IsPluginCard = isPluginCard, PluginCardId = pluginCardId,
        };
        Occupy(w);
        HomeWidgets.Add(w);
        RecalcGridGeometry();
        SaveLayout();
        return true;
    }

    public void RemoveWidget(PlacedWidget w)
    {
        if (w == null || !HomeWidgets.Contains(w)) return;
        Free(w);
        HomeWidgets.Remove(w);
        RecalcGridGeometry();
        SaveLayout();
    }

    /// <summary>调整尺寸：1×1 / 1×2 / 2×1 / 2×2。优先保持左上角，冲突则自动另寻空位。</summary>
    public void ResizeWidget(PlacedWidget w, int cols, int rows)
    {
        if (w == null || !HomeWidgets.Contains(w)) return;
        cols = Math.Clamp(cols, 1, 2);
        rows = Math.Clamp(rows, 1, 2);
        if (cols == w.Cols && rows == w.Rows) return;
        Free(w);
        if (CanPlaceAt(w.Row, w.Col, cols, rows))
        {
            w.Cols = cols; w.Rows = rows; Occupy(w);
        }
        else
        {
            var found = FindFree(cols, rows);
            if (found == null) { Occupy(w); return; }
            w.Cols = cols; w.Rows = rows; w.Row = found.Value.row; w.Col = found.Value.col; Occupy(w);
        }
        RecalcGridGeometry();
        SaveLayout();
    }

    /// <summary>在网格内移动小组件（拖拽落点）。</summary>
    public void MoveWidget(PlacedWidget w, int row, int col)
    {
        if (w == null || !HomeWidgets.Contains(w)) return;
        if (row == w.Row && col == w.Col) return;
        Free(w);
        if (CanPlaceAt(row, col, w.Cols, w.Rows))
        {
            w.Row = row; w.Col = col; Occupy(w);
        }
        else
        {
            var found = FindFree(w.Cols, w.Rows);
            if (found == null) { Occupy(w); return; }
            w.Row = found.Value.row; w.Col = found.Value.col; Occupy(w);
        }
        RecalcGridGeometry();
        SaveLayout();
    }

    // ---------- 拖动让位（编辑主页时，其他卡片自动避让） ----------

    /// <summary>
    /// 计算让位布局：被拖卡片占住 (row,col) 槽位后，其余卡片按原顺序紧凑让位到不重叠的位置。
    /// 返回所有卡片（含被拖卡片）的目标 (Row, Col)，供拖动时做避让动画、松手时整体提交。
    /// </summary>
    public Dictionary<string, (int Row, int Col)> ComputeAvoidLayout(string dragInstanceId, int row, int col)
    {
        var drag = HomeWidgets.FirstOrDefault(w => w.InstanceId == dragInstanceId);
        if (drag == null) return CurrentPositions();
        col = Math.Clamp(col, 0, WidgetGrid.Columns - drag.Cols);
        row = Math.Max(0, row);

        var others = HomeWidgets.Where(w => w.InstanceId != dragInstanceId).ToList();
        int cap = Math.Max(_maxRows, row + drag.Rows);

        while (cap <= 80)
        {
            var occ = new bool[cap, WidgetGrid.Columns];
            var result = new Dictionary<string, (int, int)> { [dragInstanceId] = (row, col) };
            MarkArr(occ, row, col, drag.Cols, drag.Rows);
            bool failed = false;
            foreach (var o in others)
            {
                var p = FirstFit(occ, o.Cols, o.Rows);
                if (p == null) { failed = true; break; }
                result[o.InstanceId] = p.Value;
                MarkArr(occ, p.Value.r, p.Value.c, o.Cols, o.Rows);
            }
            if (!failed) return result;
            cap += 2;
        }
        return CurrentPositions();
    }

    /// <summary>提交一次整体布局（拖放落点），并持久化。</summary>
    public void ApplyLayout(Dictionary<string, (int Row, int Col)> layout)
    {
        if (layout == null || layout.Count == 0 || HomeWidgets.Count == 0) return;
        _cellOwner.Clear();
        int maxRow = 0;
        foreach (var w in HomeWidgets)
        {
            if (layout.TryGetValue(w.InstanceId, out var p))
            {
                w.Row = p.Row;
                w.Col = p.Col;
            }
            Occupy(w);
            maxRow = Math.Max(maxRow, w.Row + w.Rows);
        }
        _maxRows = Math.Max(_maxRows, maxRow);
        RecalcGridGeometry();
        SaveLayout();
        OnPropertyChanged(nameof(HomeWidgets));
    }

    private Dictionary<string, (int Row, int Col)> CurrentPositions()
    {
        var d = new Dictionary<string, (int Row, int Col)>();
        foreach (var w in HomeWidgets) d[w.InstanceId] = (w.Row, w.Col);
        return d;
    }

    private static (int r, int c)? FirstFit(bool[,] occ, int cols, int rows)
    {
        int cap = occ.GetLength(0);
        for (int r = 0; r + rows <= cap; r++)
        for (int c = 0; c + cols <= WidgetGrid.Columns; c++)
            if (FreeArr(occ, r, c, cols, rows)) return (r, c);
        return null;
    }

    private static bool FreeArr(bool[,] occ, int r, int c, int cols, int rows)
    {
        for (int rr = r; rr < r + rows; rr++)
        for (int cc = c; cc < c + cols; cc++)
            if (occ[rr, cc]) return false;
        return true;
    }

    private static void MarkArr(bool[,] occ, int r, int c, int cols, int rows)
    {
        for (int rr = r; rr < r + rows; rr++)
        for (int cc = c; cc < c + cols; cc++)
            occ[rr, cc] = true;
    }

    // ---------- 占用矩阵 ----------
    private (int row, int col)? FindFree(int cols, int rows)
    {
        for (int r = 0; r < _maxRows; r++)
        for (int c = 0; c <= WidgetGrid.Columns - cols; c++)
            if (CanPlaceAt(r, c, cols, rows)) return (r, c);
        return null;
    }

    private bool CanPlaceAt(int row, int col, int cols, int rows, string? exceptInstance = null)
    {
        if (row < 0 || col < 0 || col + cols > WidgetGrid.Columns || row + rows > _maxRows) return false;
        for (int r = row; r < row + rows; r++)
        for (int c = col; c < col + cols; c++)
        {
            if (_cellOwner.TryGetValue((r, c), out var id) && id != exceptInstance) return false;
        }
        return true;
    }

    private void Occupy(PlacedWidget w)
    {
        for (int r = w.Row; r < w.Row + w.Rows; r++)
        for (int c = w.Col; c < w.Col + w.Cols; c++)
            _cellOwner[(r, c)] = w.InstanceId;
    }

    private void Free(PlacedWidget w)
    {
        for (int r = w.Row; r < w.Row + w.Rows; r++)
        for (int c = w.Col; c < w.Col + w.Cols; c++)
            _cellOwner.Remove((r, c));
    }

    /// <summary>
    /// 重算网格几何：每行高度取该行卡片自然高度的最大值（跨行卡片按行均摊，至少一格高），
    /// 画布高度按行累计 —— 卡片始终完整显示，无需在卡片内滚动。
    /// </summary>
    public void RecalcGridGeometry()
    {
        int maxRow = 0;
        foreach (var w in HomeWidgets) maxRow = Math.Max(maxRow, w.Row + w.Rows);
        // 行数随内容收缩（保留 2 行编辑余量，至少 6 行），删除卡片后画布高度同步变小，不再留大片空白
        _maxRows = Math.Max(6, maxRow + 2);

        var rowH = new double[_maxRows];
        for (int r = 0; r < _maxRows; r++) rowH[r] = WidgetGrid.CellH;
        foreach (var w in HomeWidgets)
        {
            double per = (w.NaturalHeight > 0 ? w.NaturalHeight : w.H) / Math.Max(1, w.Rows);
            for (int r = w.Row; r < w.Row + w.Rows && r < rowH.Length; r++)
                rowH[r] = Math.Max(rowH[r], per);
        }
        RowHeights = rowH;

        double total = 0;
        for (int r = 0; r < _maxRows; r++) total += rowH[r];
        total += Math.Max(0, _maxRows - 1) * WidgetGrid.Gap;
        double newH = Math.Max(total, 4 * (WidgetGrid.CellH + WidgetGrid.Gap));
        if (Math.Abs(newH - GridCanvasHeight) > 0.5)
        {
            GridCanvasHeight = newH;
            OnPropertyChanged(nameof(GridCanvasHeight));
        }
    }

    // ---------- 布局持久化 ----------
    private void SaveLayout()
    {
        SettingsService.S.HomeWidgetLayout = HomeWidgets
            .Select(w => new WidgetLayoutEntry { TypeKey = w.TypeKey, PluginCardId = w.PluginCardId, Row = w.Row, Col = w.Col, Cols = w.Cols, Rows = w.Rows })
            .ToList();
        SettingsService.Save();
    }

    private void LoadWidgetLayout()
    {
        HomeWidgets.Clear();
        _cellOwner.Clear();
        _maxRows = 6;
        var entries = SettingsService.S.HomeWidgetLayout.Count > 0 ? SettingsService.S.HomeWidgetLayout : BuildDefaultLayout();
        foreach (var e in entries)
        {
            var cat = AvailableWidgets.FirstOrDefault(x =>
                x.TypeKey == e.TypeKey &&
                (x.PluginCardId == null && e.PluginCardId == null || x.PluginCardId != null && x.PluginCardId == e.PluginCardId));
            if (cat == null) continue;
            int cols = Math.Clamp(e.Cols, 1, 2), rows = Math.Clamp(e.Rows, 1, 2);
            if (!CanPlaceAt(e.Row, e.Col, cols, rows))
            {
                var found = FindFree(cols, rows);
                if (found == null) continue;
                e.Row = found.Value.row; e.Col = found.Value.col;
            }
            var w = new PlacedWidget
            {
                TypeKey = cat.TypeKey, Title = cat.Title, Icon = cat.Icon,
                Row = e.Row, Col = e.Col, Cols = cols, Rows = rows,
                IsPluginCard = cat.IsPluginCard, PluginCardId = cat.PluginCardId,
            };
            Occupy(w);
            HomeWidgets.Add(w);
        }
        RecalcGridGeometry();
        OnPropertyChanged(nameof(HomeWidgets));
    }

    /// <summary>首次使用：由旧的主页区块开关生成一个默认布局。</summary>
    private List<WidgetLayoutEntry> BuildDefaultLayout()
    {
        var s = SettingsService.S;
        var wants = new List<(string tk, int cols, int rows, bool show)>
        {
            ("greeting", 1, 1, true),
            ("clock", 1, 1, true),
            ("quick_launch", 1, 1, true),
            ("pinned_instances", 2, 2, s.HomeShowInstances),
            ("calendar", 2, 2, true),
            ("recent", 2, 1, true),
            ("versions", 2, 2, s.HomeShowVersions),
            ("servers", 1, 2, s.HomeShowServers),
            ("saves", 1, 2, s.HomeShowSaves),
            ("pinned_worlds", 1, 2, s.HomeShowSaves),
            ("lan", 1, 1, s.HomeShowLan),
            ("monitor", 2, 1, true),
        };
        var occ = new bool[10, WidgetGrid.Columns];
        var result = new List<WidgetLayoutEntry>();
        foreach (var (tk, cols, rows, show) in wants)
        {
            if (!show) continue;
            bool placed = false;
            for (int r = 0; r < occ.GetLength(0) && !placed; r++)
            for (int c = 0; c <= WidgetGrid.Columns - cols && !placed; c++)
            {
                if (!FreeAt(occ, r, c, cols, rows)) continue;
                Mark(occ, r, c, cols, rows);
                result.Add(new WidgetLayoutEntry { TypeKey = tk, Row = r, Col = c, Cols = cols, Rows = rows });
                placed = true;
            }
        }
        return result;
    }

    private static bool FreeAt(bool[,] occ, int r, int c, int cols, int rows)
    {
        for (int rr = r; rr < r + rows; rr++)
        for (int cc = c; cc < c + cols; cc++)
            if (occ[rr, cc]) return false;
        return true;
    }

    private static void Mark(bool[,] occ, int r, int c, int cols, int rows)
    {
        for (int rr = r; rr < r + rows; rr++)
        for (int cc = c; cc < c + cols; cc++)
            occ[rr, cc] = true;
    }
}
