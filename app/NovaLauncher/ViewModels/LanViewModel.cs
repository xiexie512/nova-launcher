using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>虚拟局域网页：驱动 ZeroTier，显示引擎状态、已加入网络、虚拟 IP，支持加入/离开网络。</summary>
public sealed partial class LanViewModel : ObservableObject
{
    private string _networkIdInput = "";
    private string _statusText = "";
    private string _engineStatus = "未检测";
    private string _engineVersion = "-";
    private string _nodeId = "-";
    private string _myIp = "尚未分配";
    private bool _installed;
    private bool _busy;
    private string _lastMessage = "";

    public ObservableCollection<ZtNetworkInfo> Networks { get; } = new();

    public string NetworkIdInput { get => _networkIdInput; set => SetProperty(ref _networkIdInput, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string EngineStatus { get => _engineStatus; set => SetProperty(ref _engineStatus, value); }
    public string EngineVersion { get => _engineVersion; set => SetProperty(ref _engineVersion, value); }
    public string NodeId { get => _nodeId; set => SetProperty(ref _nodeId, value); }
    public string MyIp { get => _myIp; set => SetProperty(ref _myIp, value); }
    public bool Installed { get => _installed; set { SetProperty(ref _installed, value); OnPropertyChanged(nameof(NotInstalled)); } }
    public bool NotInstalled => !Installed;
    public bool Busy { get => _busy; set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(NotBusy)); } }
    public bool NotBusy => !Busy;
    public string LastMessage { get => _lastMessage; set => SetProperty(ref _lastMessage, value); }
    public bool HasNetworks => Networks.Count > 0;
    public string InstallerUrl => ZeroTierService.InstallerUrl;
    /// <summary>当前电脑匹配的 ZeroTier 平台信息（操作系统 · 架构），用于「按电脑版本下载」提示。</summary>
    public string PlatformText => $"{ZeroTierService.OsLabel} · {ZeroTierService.ArchLabel}";

    public LanViewModel()
    {
        TokenInput = SettingsService.S.ZeroTierToken;
        _ = RefreshAsync();
        if (ZeroTierAdminService.HasToken) _ = RefreshAdminAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => Task.Run(() =>
    {
        string status, engine = "未安装", ver = "-", node = "-", ip = "未安装", msg = "";
        bool installed;
        var nets = new List<ZtNetworkInfo>();
        try
        {
            ZeroTierService.Detect();
            installed = ZeroTierService.IsInstalled;
            if (!installed)
            {
                status = "未检测到虚拟局域网引擎 ZeroTier";
            }
            else
            {
                var info = ZeroTierService.GetInfo();
                engine = info.Online ? "已连接" : "离线";
                ver = info.Version;
                node = info.NodeId;
                nets = ZeroTierService.GetNetworks();
                var myIp = ZeroTierService.MyVirtualIp();
                ip = string.IsNullOrEmpty(myIp) ? "尚未分配" : myIp;
                status = nets.Count > 0
                    ? $"已加入 {nets.Count} 个虚拟局域网" + (string.IsNullOrEmpty(myIp) ? "" : $" · 虚拟 IP {myIp}")
                    : "引擎运行中，尚未加入任何网络";
            }
        }
        catch (Exception e)
        {
            installed = true;
            engine = "已安装";
            status = "读取引擎信息失败：" + e.Message;
            msg = ZeroTierService.LastError ?? e.Message;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Busy = true;
            try
            {
                Installed = installed;
                EngineStatus = engine;
                EngineVersion = ver;
                NodeId = node;
                MyIp = ip;
                StatusText = status;
                LastMessage = msg;
                Networks.Clear();
                foreach (var n in nets) Networks.Add(n);
                OnPropertyChanged(nameof(HasNetworks));
            }
            finally { Busy = false; }
        });
    });

    [RelayCommand]
    private async Task JoinAsync()
    {
        var id = NetworkIdInput.Trim();
        if (id.Length == 0) { LastMessage = "请输入 16 位网络 ID（在 my.zerotier.com 创建网络后可见）"; return; }
        Busy = true;
        LastMessage = "";
        try
        {
            ZeroTierService.JoinNetwork(id);
            LastMessage = $"已请求加入网络 {id}。若需通过认证，请到 my.zerotier.com 的成员列表勾选 Auth。";
            NetworkIdInput = "";
            await RefreshAsync();
        }
        catch (Exception e) { LastMessage = "加入失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task LeaveAsync(string id)
    {
        Busy = true;
        try
        {
            ZeroTierService.LeaveNetwork(id);
            LastMessage = $"已离开网络 {id}";
            await RefreshAsync();
        }
        catch (Exception e) { LastMessage = "操作失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        Busy = true;
        LastMessage = $"正在下载官方安装包（{ZeroTierService.OsLabel} / {ZeroTierService.ArchLabel}，约 30MB）…";
        try
        {
            var path = await ZeroTierService.DownloadAndOpenInstallerAsync();
            LastMessage = $"已按当前电脑（{ZeroTierService.OsLabel} / {ZeroTierService.ArchLabel}）匹配安装包并打开：{path}\n请按系统向导完成安装，然后回到本页点击「刷新」。";
        }
        catch (Exception e) { LastMessage = "下载失败：" + e.Message; }
        finally { Busy = false; }
    }

    // ============ 后台管理（Central 成员授权） ============

    private string _tokenInput = "";
    private bool _adminBusy;
    private string _adminMessage = "";
    private ZtAdminNetwork? _selectedNetwork;

    public ObservableCollection<ZtAdminNetwork> AdminNetworks { get; } = new();
    public ObservableCollection<ZtAdminMember> Members { get; } = new();

    public string TokenInput { get => _tokenInput; set => SetProperty(ref _tokenInput, value); }
    public bool AdminBusy { get => _adminBusy; set { SetProperty(ref _adminBusy, value); OnPropertyChanged(nameof(NotAdminBusy)); } }
    public bool NotAdminBusy => !AdminBusy;
    public string AdminMessage { get => _adminMessage; set => SetProperty(ref _adminMessage, value); }
    public bool HasToken => ZeroTierAdminService.HasToken;
    public bool NoToken => !HasToken;
    public bool HasMembers => Members.Count > 0;

    public ZtAdminNetwork? SelectedNetwork
    {
        get => _selectedNetwork;
        set
        {
            if (SetProperty(ref _selectedNetwork, value) && value != null)
                _ = LoadMembersAsync(value.Id);
        }
    }

    [RelayCommand]
    private async Task SaveTokenAsync()
    {
        var t = TokenInput.Trim();
        if (t.Length == 0) { AdminMessage = "请先粘贴 API Token（my.zerotier.com → Account 页面生成）"; return; }
        AdminBusy = true;
        AdminMessage = "";
        try
        {
            ZeroTierAdminService.SaveToken(t);
            OnPropertyChanged(nameof(HasToken));
            OnPropertyChanged(nameof(NoToken));
            await RefreshAdminAsync();
            AdminMessage = "Token 已保存，网络列表已加载";
        }
        catch (Exception e) { AdminMessage = "Token 校验失败：" + e.Message; }
        finally { AdminBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshAdminAsync()
    {
        if (!ZeroTierAdminService.HasToken) return;
        AdminBusy = true;
        AdminMessage = "";
        try
        {
            var nets = await ZeroTierAdminService.GetNetworksAsync();
            var prev = SelectedNetwork?.Id;
            AdminNetworks.Clear();
            foreach (var n in nets) AdminNetworks.Add(n);
            SelectedNetwork = (prev != null ? AdminNetworks.FirstOrDefault(n => n.Id == prev) : null)
                              ?? AdminNetworks.FirstOrDefault();
            if (SelectedNetwork == null)
            {
                Members.Clear();
                OnPropertyChanged(nameof(HasMembers));
                AdminMessage = "该账号下暂无网络";
            }
        }
        catch (Exception e) { AdminMessage = "加载网络失败：" + e.Message; }
        finally { AdminBusy = false; }
    }

    private async Task LoadMembersAsync(string networkId)
    {
        AdminBusy = true;
        AdminMessage = "";
        try
        {
            var ms = await ZeroTierAdminService.GetMembersAsync(networkId);
            Members.Clear();
            foreach (var m in ms) Members.Add(m);
            OnPropertyChanged(nameof(HasMembers));
            if (ms.Count == 0) AdminMessage = "该网络暂无成员（新设备加入并请求授权后才会出现）";
        }
        catch (Exception e) { AdminMessage = "加载成员失败：" + e.Message; }
        finally { AdminBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleAuthAsync(ZtAdminMember member)
    {
        if (SelectedNetwork == null) return;
        AdminBusy = true;
        AdminMessage = "";
        try
        {
            await ZeroTierAdminService.SetAuthorizedAsync(SelectedNetwork.Id, member.NodeId, !member.Authorized);
            AdminMessage = member.Authorized ? $"已取消授权 {member.DisplayName}" : $"已授权 {member.DisplayName}，设备可立即接入";
            await LoadMembersAsync(SelectedNetwork.Id);
        }
        catch (Exception e) { AdminMessage = "操作失败：" + e.Message; }
        finally { AdminBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(ZtAdminMember member)
    {
        if (SelectedNetwork == null) return;
        AdminBusy = true;
        AdminMessage = "";
        try
        {
            await ZeroTierAdminService.RemoveMemberAsync(SelectedNetwork.Id, member.NodeId);
            AdminMessage = $"已移除成员 {member.DisplayName}（设备需重新 join 才能回来）";
            await LoadMembersAsync(SelectedNetwork.Id);
        }
        catch (Exception e) { AdminMessage = "移除失败：" + e.Message; }
        finally { AdminBusy = false; }
    }

    [RelayCommand]
    private void OpenAccountPage() =>
        Process.Start(new ProcessStartInfo { FileName = "https://my.zerotier.com/account", UseShellExecute = true });
}
