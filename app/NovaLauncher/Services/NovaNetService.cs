using System;
using NovaNet.Engine;

namespace NovaLauncher.Services;

/// <summary>
/// NovaNet 自研虚拟局域网（替代 ZeroTier）：管理引擎生命周期与设置持久化，
/// 把引擎事件统一转发给 UI（主页卡片、虚拟局域网页）。
/// </summary>
public static class NovaNetService
{
    private static NnEngine? _engine;
    private static readonly object Lock = new();

    // ---- 转发给 UI 的事件（引擎线程触发，UI 侧自行 Post 到主线程） ----
    public static event Action<NnState, string>? StateChanged;
    public static event Action<NnRoom?>? RoomChanged;
    public static event Action<IReadOnlyList<NnMember>>? MembersChanged;
    public static event Action<string, string>? PeerPathChanged;
    public static event Action<string>? Log;
    public static event Action<string>? Kicked;

    public static NnEngine Engine
    {
        get
        {
            lock (Lock) return _engine ??= CreateEngine();
        }
    }

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SettingsService.S.NovaNetServer);

    private static NnEngine CreateEngine()
    {
        var s = SettingsService.S;
        EnsureClientId(s);
        var cfg = new NovaNetConfig
        {
            ServerUri = ToWsAddress(s.NovaNetServer),
            ClientId = s.NovaNetClientId,
            Name = DisplayName(s),
            LocalGamePort = s.NovaNetLocalGamePort > 0 ? s.NovaNetLocalGamePort : 25565,
            AutoJoinCode = string.IsNullOrEmpty(s.NovaNetLastRoomCode) ? null : s.NovaNetLastRoomCode,
            AutoJoinPassword = string.IsNullOrEmpty(s.NovaNetLastRoomPassword) ? null : s.NovaNetLastRoomPassword,
        };
        var e = new NnEngine(cfg);
        e.StateChanged += (st, msg) => StateChanged?.Invoke(st, msg);
        e.RoomChanged += r => RoomChanged?.Invoke(r);
        e.MembersChanged += m => MembersChanged?.Invoke(m);
        e.PeerPathChanged += (id, path) => PeerPathChanged?.Invoke(id, path);
        e.Log += m => { LogService.Info("[NovaNet] " + m); Log?.Invoke(m); };
        e.Kicked += reason => Kicked?.Invoke(reason);
        return e;
    }

    /// <summary>若设置里没有客户端标识则生成并持久化（16 位十六进制）。</summary>
    private static void EnsureClientId(Models.LauncherSettings s)
    {
        if (s.NovaNetClientId.Length >= 16) return;
        s.NovaNetClientId = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()[..16];
        SettingsService.Save();
    }

    /// <summary>房间内显示名：优先离线用户名，否则 玩家+短ID。</summary>
    public static string DisplayName(Models.LauncherSettings s)
    {
        var name = (s.OfflineUsername ?? "").Trim();
        if (name.Length > 0) return name;
        var id = s.NovaNetClientId;
        return "玩家" + (id.Length >= 4 ? id[^4..].ToUpperInvariant() : "0000");
    }

    /// <summary>把用户填的地址规整成 ws://host:port。</summary>
    public static string ToWsAddress(string addr)
    {
        addr = (addr ?? "").Trim();
        if (addr.Length == 0) return "";
        if (!addr.Contains("://")) addr = "ws://" + addr;
        try
        {
            var uri = new Uri(addr);
            return $"ws://{uri.Host}:{(uri.Port > 0 ? uri.Port : NnConst.DefaultPort)}";
        }
        catch
        {
            return addr;
        }
    }

    /// <summary>启动引擎（应用启动时调用；未配置 Hub 地址则跳过）。</summary>
    public static void Start()
    {
        if (!IsConfigured) return;
        var e = Engine;
        if (e.State == NnState.Stopped) e.Start();
    }

    /// <summary>保存 Hub 地址/本地游戏端口，重建引擎并连接。</summary>
    public static void ConfigureAndConnect(string address, int localGamePort)
    {
        var s = SettingsService.S;
        s.NovaNetServer = ToWsAddress(address);
        s.NovaNetLocalGamePort = localGamePort > 0 ? localGamePort : 25565;
        SettingsService.Save();

        lock (Lock)
        {
            var old = _engine;
            _engine = null;
            if (old != null) _ = old.StopAsync();
        }
        Engine.Start();
    }

    /// <summary>断开当前连接并重连（重新读取配置）。</summary>
    public static void Reconnect()
    {
        lock (Lock)
        {
            var old = _engine;
            _engine = null;
            if (old != null) _ = old.StopAsync();
        }
        if (IsConfigured) Engine.Start();
    }

    public static void SaveLastRoom(string? code, string? password)
    {
        var s = SettingsService.S;
        s.NovaNetLastRoomCode = code ?? "";
        s.NovaNetLastRoomPassword = password ?? "";
        SettingsService.Save();
    }

    public static void ClearLastRoom()
    {
        var s = SettingsService.S;
        s.NovaNetLastRoomCode = "";
        s.NovaNetLastRoomPassword = "";
        SettingsService.Save();
    }
}
