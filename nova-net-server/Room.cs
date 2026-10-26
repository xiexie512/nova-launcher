namespace NovaNet.Server;

/// <summary>房间内的一个成员（服务端视角）。</summary>
public sealed class MemberRecord
{
    public required string MemberId { get; init; }
    public required string ClientId { get; init; }
    public required string Name { get; init; }
    public required string VirtualIp { get; init; }
    public string PublicUdp { get; set; } = "";
    public string LocalUdp { get; set; } = "";
    public bool IsOwner { get; init; }
    public long JoinedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public HubConnection? Conn { get; set; }
}

/// <summary>一个房间（虚拟局域网）。</summary>
public sealed class Room
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? PasswordHash { get; set; }
    public string OwnerMemberId { get; set; } = "";
    public List<MemberRecord> Members { get; } = new();
    public HashSet<int> UsedIps { get; } = new();
    public int MaxMembers { get; set; } = 20;
}
