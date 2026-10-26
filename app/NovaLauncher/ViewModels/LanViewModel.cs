using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;
using NovaNet.Engine;

namespace NovaLauncher.ViewModels;

/// <summary>
/// 虚拟局域网页：驱动自研 NovaNet（Hub 信令 + UDP 打洞直连 + 中继回退 + 本地端口映射）。
/// 建房/加入房间（房间码 + 密码）→ 成员互见虚拟 IP → 隧道打通 → 在游戏内添加服务器 127.0.0.1:200xx。
/// </summary>
public sealed partial class LanViewModel : ObservableObject
{
    private string _hubAddress = "";
    private string _localGamePortText = "25565";
    private string _connectionStatus = "未连接";
    private string _clientId = "-";
    private string _roomName = "-";
    private string _roomCode = "-";
    private string _myVirtualIp = "尚未分配";
    private bool _inRoom;
    private bool _isOwner;
    private bool _busy;
    private string _lastMessage = "";
    private string _createName = "";
    private string _createPassword = "";
    private string _joinCode = "";
    private string _joinPassword = "";

    public ObservableCollection<NnMember> Members { get; } = new();

    public LanViewModel()
    {
        var s = SettingsService.S;
        HubAddress = s.NovaNetServer;
        LocalGamePortText = (s.NovaNetLocalGamePort > 0 ? s.NovaNetLocalGamePort : 25565).ToString();
        NovaNetService.StateChanged += (st, msg) => Dispatcher.UIThread.Post(() => ApplyState(st, msg));
        NovaNetService.RoomChanged += room => Dispatcher.UIThread.Post(() => ApplyRoom(room));
        NovaNetService.MembersChanged += ms => Dispatcher.UIThread.Post(() => SyncMembers(ms));
        NovaNetService.Kicked += reason => Dispatcher.UIThread.Post(() => LastMessage = $"你已被房主移出房间：{reason}");
        NovaNetService.Log += m => Dispatcher.UIThread.Post(() => LastMessage = m);
        NovaNetService.Start();
        ApplyState(NovaNetService.Engine.State, "");
        ApplyRoom(NovaNetService.Engine.Room);
        SyncMembers(NovaNetService.Engine.Members);
    }

    // ================= 连接配置 =================

    public string HubAddress { get => _hubAddress; set => SetProperty(ref _hubAddress, value); }
    public string LocalGamePortText { get => _localGamePortText; set => SetProperty(ref _localGamePortText, value); }

    public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value); }
    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }

    public string RoomName { get => _roomName; set => SetProperty(ref _roomName, value); }
    public string RoomCode { get => _roomCode; set => SetProperty(ref _roomCode, value); }
    public string MyVirtualIp { get => _myVirtualIp; set => SetProperty(ref _myVirtualIp, value); }

    public bool InRoom { get => _inRoom; set { SetProperty(ref _inRoom, value); OnPropertyChanged(nameof(NotInRoom)); } }
    public bool NotInRoom => !InRoom;
    public bool IsOwner { get => _isOwner; set { SetProperty(ref _isOwner, value); OnPropertyChanged(nameof(CanKick)); } }
    public bool CanKick => IsOwner;
    public bool Busy { get => _busy; set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(NotBusy)); } }
    public bool NotBusy => !Busy;
    public string LastMessage { get => _lastMessage; set => SetProperty(ref _lastMessage, value); }
    public bool HasMembers => Members.Count > 0;
    public string ConnectedAtText => "NovaNet 自研虚拟局域网";

    public string CreateName { get => _createName; set => SetProperty(ref _createName, value); }
    public string CreatePassword { get => _createPassword; set => SetProperty(ref _createPassword, value); }
    public string JoinCode { get => _joinCode; set => SetProperty(ref _joinCode, value); }
    public string JoinPassword { get => _joinPassword; set => SetProperty(ref _joinPassword, value); }

    private void ApplyState(NnState st, string msg)
    {
        ConnectionStatus = st switch
        {
            NnState.Stopped => string.IsNullOrWhiteSpace(HubAddress) ? "未配置 Hub 地址" : "已停止",
            NnState.Connecting => "连接中…",
            NnState.Online => "已连接",
            NnState.Reconnecting => "连接断开，自动重连中…",
            _ => st.ToString(),
        };
        ClientId = NovaNetService.Engine.Config.ClientId;
        if (string.IsNullOrEmpty(ClientId)) ClientId = "-";
        if (!string.IsNullOrEmpty(msg)) LastMessage = msg;
    }

    private void ApplyRoom(NnRoom? room)
    {
        if (room == null)
        {
            InRoom = false;
            IsOwner = false;
            RoomName = "-";
            RoomCode = "-";
            MyVirtualIp = "尚未分配";
            Members.Clear();
            OnPropertyChanged(nameof(HasMembers));
            return;
        }
        InRoom = true;
        IsOwner = room.IsOwner;
        RoomName = room.Name;
        RoomCode = room.Code;
        MyVirtualIp = room.MyVirtualIp;
    }

    private void SyncMembers(System.Collections.Generic.IReadOnlyList<NnMember> ms)
    {
        Members.Clear();
        foreach (var m in ms.OrderBy(x => x.VirtualIp)) Members.Add(m);
        OnPropertyChanged(nameof(HasMembers));
    }

    // ================= 命令 =================

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var addr = HubAddress.Trim();
        if (addr.Length == 0) { LastMessage = "请填写 Hub 服务器地址（例如 hub.example.com:8535）"; return; }
        if (!int.TryParse(LocalGamePortText.Trim(), out var port) || port <= 0 || port > 65535)
        { LastMessage = "本地游戏端口无效（1-65535，Minecraft 默认 25565）"; return; }
        Busy = true;
        LastMessage = "";
        try
        {
            NovaNetService.ConfigureAndConnect(addr, port);
            await Task.Delay(200);
            LastMessage = $"已保存并连接 {NovaNetService.ToWsAddress(addr)}，本地游戏端口 {port}";
        }
        catch (Exception e) { LastMessage = "连接失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private Task ReconnectAsync()
    {
        Busy = true;
        LastMessage = "";
        try
        {
            NovaNetService.Reconnect();
            LastMessage = "已重连";
        }
        catch (Exception e) { LastMessage = "重连失败：" + e.Message; }
        finally { Busy = false; }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        var name = CreateName.Trim();
        if (name.Length == 0) name = NovaNetService.DisplayName(SettingsService.S) + " 的房间";
        if (name.Length > 32) name = name[..32];
        Busy = true;
        LastMessage = "";
        try
        {
            var engine = NovaNetService.Engine;
            if (engine.State == NnState.Stopped) engine.Start();
            var code = await engine.CreateRoomAsync(name, string.IsNullOrWhiteSpace(CreatePassword) ? null : CreatePassword.Trim());
            NovaNetService.SaveLastRoom(code, string.IsNullOrWhiteSpace(CreatePassword) ? null : CreatePassword.Trim());
            LastMessage = $"房间已创建，房间码 {code}。把房间码（和密码）发给好友，让他们在「加入房间」里输入即可。";
            CreateName = "";
            CreatePassword = "";
        }
        catch (Exception e) { LastMessage = "创建房间失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        var code = JoinCode.Trim().ToUpperInvariant();
        if (code.Length != 6) { LastMessage = "房间码是 6 位，请向房主确认（例如 AB12CD）"; return; }
        Busy = true;
        LastMessage = "";
        try
        {
            var engine = NovaNetService.Engine;
            if (engine.State == NnState.Stopped) engine.Start();
            await engine.JoinRoomAsync(code, string.IsNullOrWhiteSpace(JoinPassword) ? null : JoinPassword.Trim());
            NovaNetService.SaveLastRoom(code, string.IsNullOrWhiteSpace(JoinPassword) ? null : JoinPassword.Trim());
            LastMessage = $"已加入房间 {code}。等待隧道打通后，在游戏「多人游戏→添加服务器」输入好友的联机地址即可。";
            JoinCode = "";
            JoinPassword = "";
        }
        catch (Exception e) { LastMessage = "加入失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        Busy = true;
        LastMessage = "";
        try
        {
            await NovaNetService.Engine.LeaveRoomAsync();
            NovaNetService.ClearLastRoom();
            LastMessage = "已离开房间";
        }
        catch (Exception e) { LastMessage = "操作失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task KickAsync(NnMember member)
    {
        if (!IsOwner) return;
        Busy = true;
        try
        {
            await NovaNetService.Engine.KickAsync(member.MemberId);
            LastMessage = $"已将 {member.Name} 移出房间";
        }
        catch (Exception e) { LastMessage = "操作失败：" + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task CopyMyIpAsync()
    {
        if (MyVirtualIp.StartsWith("尚未")) { LastMessage = "加入房间后才能看到虚拟 IP"; return; }
        var top = Avalonia.Controls.TopLevel.GetTopLevel(Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l ? l.MainWindow : null);
        if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(MyVirtualIp);
        LastMessage = $"已复制虚拟 IP {MyVirtualIp}，对方在游戏内添加服务器时填写这个地址（连不上就改用对方的联机地址 127.0.0.1:200xx）";
    }

    [RelayCommand]
    private async Task CopyMemberAddressAsync(NnMember member)
    {
        if (string.IsNullOrEmpty(member.ConnectAddress)) { LastMessage = "对方映射端口尚未就绪，稍等片刻再试"; return; }
        var top = Avalonia.Controls.TopLevel.GetTopLevel(Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l ? l.MainWindow : null);
        if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(member.ConnectAddress);
        LastMessage = $"已复制 {member.Name} 的联机地址 {member.ConnectAddress}，在游戏「多人游戏→添加服务器」粘贴即可";
    }
}
