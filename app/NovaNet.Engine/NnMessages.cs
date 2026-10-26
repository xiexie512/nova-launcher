using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaNet.Engine;

/// <summary>JSON 序列化选项（camelCase）。</summary>
public static class NnJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>客户端 → Hub 的信令消息。</summary>
public sealed class NnMsg
{
    [JsonPropertyName("t")] public string T { get; set; } = "";

    // hello / 端点
    public string? ClientId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? PublicUdp { get; set; }
    public string? LocalUdp { get; set; }

    // create / join
    public string? RoomName { get; set; }
    public string? Password { get; set; }
    public string? Code { get; set; }

    // kick / punch / punch_ok / relay
    public string? Target { get; set; }
    public string? MemberId { get; set; }
    [JsonPropertyName("data")] public string? DataB64 { get; set; }
}

/// <summary>Hub → 客户端的信令消息（收包用 JsonDocument 直接分派，这里仅定义成员/房间 DTO）。</summary>
public sealed class NnMemberDto
{
    public string MemberId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VirtualIp { get; set; } = "";
    public string PublicUdp { get; set; } = "";
    public string LocalUdp { get; set; } = "";
    public bool Online { get; set; } = true;
    public bool IsOwner { get; set; }
    public long JoinedAt { get; set; }
}

public sealed class NnRoomDto
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string MyVirtualIp { get; set; } = "";
    public bool IsOwner { get; set; }
    public bool HasPassword { get; set; }
}
