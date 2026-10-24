using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

/// <summary>ZeroTier Central 网络（后台管理用）。</summary>
public sealed class ZtAdminNetwork
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int TotalMembers { get; set; }
    public int AuthorizedMembers { get; set; }
    public int OnlineMembers { get; set; }
    public string Title => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    public string Summary => $"成员 {TotalMembers} · 已授权 {AuthorizedMembers} · 在线 {OnlineMembers}";
}

/// <summary>ZeroTier Central 网络成员（后台管理用）。</summary>
public sealed class ZtAdminMember
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Authorized { get; set; }
    public string VirtualIp { get; set; } = "";
    public string PhysicalAddress { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public string LastOnlineText { get; set; } = "";
    public string OnlineText { get; set; } = "";
    public bool IsOnline { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? NodeId : Name;
    public string AuthText => Authorized ? "已授权" : "未授权";
    public string AuthToggleText => Authorized ? "取消授权" : "授权";
}

/// <summary>
/// ZeroTier 后台管理：读取网络与成员、授权/取消授权、移除成员。
/// 使用 Central 个人 API Token（免费版可用），在 my.zerotier.com → Account 页面生成。
/// 鉴权格式为 Legacy Central 的 "Authorization: token xxx"。
/// </summary>
public static class ZeroTierAdminService
{
    private const string Base = "https://api.zerotier.com/api/v1";

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Base),
        Timeout = TimeSpan.FromSeconds(30),
    };

    static ZeroTierAdminService() => ApplyAuth();

    /// <summary>当前已保存的 Token（空串表示未配置）。</summary>
    public static string Token => (SettingsService.S.ZeroTierToken ?? "").Trim();

    public static bool HasToken => Token.Length > 0;

    /// <summary>保存 Token 到本地设置并生效。</summary>
    public static void SaveToken(string token)
    {
        SettingsService.S.ZeroTierToken = (token ?? "").Trim();
        SettingsService.Save();
        ApplyAuth();
    }

    private static void ApplyAuth()
    {
        Http.DefaultRequestHeaders.Remove("Authorization");
        if (Token.Length > 0)
            Http.DefaultRequestHeaders.Add("Authorization", "token " + Token);
    }

    private static async Task<string> SendAsync(HttpRequestMessage req)
    {
        try
        {
            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var hint = resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden
                    ? "Token 无效或已失效，请到 my.zerotier.com → Account 重新生成后保存"
                    : $"API 返回 {(int)resp.StatusCode}";
                throw new Exception(hint + (string.IsNullOrWhiteSpace(body) ? "" : "：" + body.Trim()));
            }
            return body;
        }
        catch (HttpRequestException e)
        {
            throw new Exception("无法连接 ZeroTier Central：请检查网络后重试", e);
        }
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    private static string Get(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    /// <summary>当前账号可管理的网络列表。</summary>
    public static async Task<List<ZtAdminNetwork>> GetNetworksAsync()
    {
        var body = await SendAsync(Req(HttpMethod.Get, "/network"));
        using var doc = JsonDocument.Parse(body);
        var list = new List<ZtAdminNetwork>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var cfg = el.TryGetProperty("config", out var c) ? c : default;
            list.Add(new ZtAdminNetwork
            {
                Id = Get(el, "id"),
                Name = cfg.ValueKind == JsonValueKind.Object ? Get(cfg, "name") : "",
                TotalMembers = GetInt(el, "totalMemberCount"),
                AuthorizedMembers = GetInt(el, "authorizedMemberCount"),
                OnlineMembers = GetInt(el, "onlineMemberCount"),
            });
        }
        return list;
    }

    /// <summary>指定网络的成员列表。</summary>
    public static async Task<List<ZtAdminMember>> GetMembersAsync(string networkId)
    {
        var body = await SendAsync(Req(HttpMethod.Get, $"/network/{networkId}/member"));
        using var doc = JsonDocument.Parse(body);
        var list = new List<ZtAdminMember>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var cfg = el.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var authorized = cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("authorized", out var au) && au.ValueKind == JsonValueKind.True;
            var ips = new List<string>();
            if (cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("ipAssignments", out var ia))
                foreach (var a in ia.EnumerateArray())
                    if (a.GetString() is { Length: > 0 } s) ips.Add(s);
            var lastOnline = GetLong(el, "lastOnline");
            var m = new ZtAdminMember
            {
                NodeId = Get(el, "nodeId"),
                Name = Get(el, "name"),
                Authorized = authorized,
                VirtualIp = string.Join("、", ips),
                PhysicalAddress = Get(el, "physicalAddress"),
                ClientVersion = Get(el, "clientVersion"),
                LastOnlineText = lastOnline > 0 ? ToLocalTimeText(lastOnline) : "从未在线",
                IsOnline = lastOnline > 0 && now - lastOnline < 120_000,
            };
            m.OnlineText = m.IsOnline ? "在线" : "离线";
            list.Add(m);
        }
        return list;
    }

    /// <summary>授权 / 取消授权某成员。</summary>
    public static async Task SetAuthorizedAsync(string networkId, string nodeId, bool authorized)
    {
        var body = JsonSerializer.Serialize(new { config = new { authorized } });
        await SendAsync(Req(HttpMethod.Post, $"/network/{networkId}/member/{nodeId}", body));
    }

    /// <summary>从网络移除某成员（设备需重新加入才能回来）。</summary>
    public static async Task RemoveMemberAsync(string networkId, string nodeId)
    {
        await SendAsync(Req(HttpMethod.Delete, $"/network/{networkId}/member/{nodeId}"));
    }

    private static int GetInt(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long GetLong(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string ToLocalTimeText(long unixMs)
    {
        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
            var diff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMs;
            if (diff < 120_000) return "刚刚";
            if (diff < 3600_000) return $"{diff / 60_000} 分钟前";
            if (diff < 86400_000) return $"{diff / 3600_000} 小时前";
            return dt.ToString("MM-dd HH:mm");
        }
        catch { return "未知"; }
    }
}
