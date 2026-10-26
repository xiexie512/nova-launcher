namespace NovaNet.Engine;

/// <summary>引擎连接状态。</summary>
public enum NnState
{
    Stopped,
    Connecting,
    Online,
    /// <summary>连接断开，正在自动重连。</summary>
    Reconnecting,
}

/// <summary>房间成员（对端视角，不含自己）。</summary>
public sealed class NnMember
{
    public string MemberId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VirtualIp { get; set; } = "";
    public string PublicUdp { get; set; } = "";
    public string LocalUdp { get; set; } = "";
    public bool Online { get; set; } = true;
    public bool IsOwner { get; set; }
    public long JoinedAt { get; set; }

    /// <summary>本机为对端分配的联机映射端口（引擎绑定成功后填充）。</summary>
    public int LocalPort { get; set; }

    /// <summary>在启动器「添加服务器」时填写的联机地址。</summary>
    public string ConnectAddress => LocalPort > 0 ? $"127.0.0.1:{LocalPort}" : "";

    /// <summary>当前隧道路径（P2P 直连 / 中继）。</summary>
    public string PathText { get; set; } = "连接中";
}

/// <summary>当前房间信息。</summary>
public sealed class NnRoom
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string MyVirtualIp { get; set; } = "";
    public bool IsOwner { get; set; }
    public bool HasPassword { get; set; }
}

/// <summary>引擎配置。</summary>
public sealed class NovaNetConfig
{
    /// <summary>Hub 地址，形如 ws://hub.example.com:8535。</summary>
    public string ServerUri { get; set; } = $"ws://127.0.0.1:{NnConst.DefaultPort}";

    /// <summary>本机稳定标识（持久化，重连后保持同一身份与虚拟 IP）。</summary>
    public string ClientId { get; set; } = "";

    public string Name { get; set; } = "Player";

    /// <summary>本机 Minecraft 服务端端口（对局域网开放的端口），隧道数据最终转发到这里。</summary>
    public int LocalGamePort { get; set; } = 25565;

    /// <summary>自动重连后要加入的房间码（可为空）。</summary>
    public string? AutoJoinCode { get; set; }

    public string? AutoJoinPassword { get; set; }

    /// <summary>引擎探测到的公网 UDP 端点（STUN，引擎内部填充）。</summary>
    public string? PublicUdp { get; set; }

    /// <summary>引擎绑定的本地 UDP 端点（引擎内部填充）。</summary>
    public string? LocalUdp { get; set; }

    /// <summary>禁用 P2P（测试中继链路时用）。</summary>
    public bool DisableP2P { get; set; }
}
