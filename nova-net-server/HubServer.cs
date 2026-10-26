using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaNet.Engine;

namespace NovaNet.Server;

/// <summary>单个 WS 连接（含其所在房间的成员引用）。</summary>
public sealed class HubConnection
{
    public required System.Net.WebSockets.WebSocket Ws { get; init; }
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "玩家";
    public string PublicUdp { get; set; } = "";
    public string LocalUdp { get; set; } = "";
    public MemberRecord? Member { get; set; }
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendJsonAsync(object payload)
    {
        if (Ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(payload, NnJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync();
        try { await Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    public void SendBinary(byte[] bytes)
    {
        if (Ws.State != WebSocketState.Open) return;
        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync();
            try { await Ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None); }
            catch { }
            finally { _sendLock.Release(); }
        });
    }
}

/// <summary>NovaNet Hub：WS 信令 + 中继转发 + HTTP 状态页 + UDP STUN。</summary>
public static class HubServer
{
    private static readonly RoomManager Rooms = new(maxMembersPerRoom: 20);
    private static readonly ConcurrentDictionary<string, HubConnection> Conns = new();

    public static async Task RunAsync(int port, int udpPort, string bind)
    {
        _ = Task.Run(() => StunServer.RunAsync(udpPort, bind));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://{bind}:{port}");
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/", () => Results.Json(new
        {
            name = "NovaNet Hub",
            version = "1.0.0",
            protocol = NnConst.Version,
            tcpPort = port,
            udpPort = udpPort,
            rooms = Rooms.RoomCount,
            members = Rooms.MemberCount,
            connections = Conns.Count,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }));

        app.MapGet("/healthz", () => Results.Text("ok"));

        app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleAsync(ws);
        });

        await app.RunAsync();
    }

    public static async Task SendAsync(HubConnection? conn, object payload)
    {
        if (conn == null) return;
        try { await conn.SendJsonAsync(payload); } catch { }
    }

    private static async Task HandleAsync(System.Net.WebSockets.WebSocket ws)
    {
        var conn = new HubConnection { Ws = ws };
        var buf = new byte[8192];
        var text = new MemoryStream();
        var binary = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var seg = new ArraySegment<byte>(buf);
                var res = await ws.ReceiveAsync(seg, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;

                if (res.MessageType == WebSocketMessageType.Binary)
                {
                    binary.Write(buf, 0, res.Count);
                    if (res.EndOfMessage)
                    {
                        var frameBytes = binary.ToArray();
                        binary.SetLength(0);
                        // 中继二进制帧
                        HandleRelayBinary(conn, frameBytes);
                    }
                    continue;
                }

                text.Write(buf, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var json = Encoding.UTF8.GetString(text.ToArray());
                text.SetLength(0);
                await HandleJsonAsync(conn, json);
            }
        }
        catch { }
        finally
        {
            if (conn.Member != null) Rooms.OnDisconnect(conn);
            else if (conn.ClientId.Length > 0 && Conns.TryGetValue(conn.ClientId, out var c) && c == conn)
                Conns.TryRemove(conn.ClientId, out _);
            try { ws.Dispose(); } catch { }
        }
    }

    private static async Task HandleJsonAsync(HubConnection conn, string json)
    {
        NnMsg msg;
        try { msg = JsonSerializer.Deserialize<NnMsg>(json, NnJson.Options) ?? new NnMsg(); }
        catch { return; }

        switch (msg.T)
        {
            case "hello":
                await OnHelloAsync(conn, msg);
                break;
            case "create":
                await OnCreateAsync(conn, msg);
                break;
            case "join":
                await OnJoinAsync(conn, msg);
                break;
            case "leave":
                Rooms.Leave(conn, "left");
                await conn.SendJsonAsync(new { t = "left" });
                break;
            case "kick":
                await OnKickAsync(conn, msg);
                break;
            case "members":
                await ResendRoomAsync(conn);
                break;
            case "punch":
                Rooms.ForwardPunch(conn, msg.Target ?? "", "punch_req");
                break;
            case "punch_ok":
                Rooms.ForwardPunch(conn, msg.Target ?? "", "punch_resp");
                break;
            case "relay":
                await OnRelayAsync(conn, msg);
                break;
            case "ping":
                await conn.SendJsonAsync(new { t = "pong" });
                break;
        }
    }

    private static void HandleRelayBinary(HubConnection conn, byte[] frameBytes)
    {
        try
        {
            var frame = NnFrame.Decode(frameBytes);
            Rooms.Relay(conn, frameBytes, frame.To);
        }
        catch { }
    }

    private static async Task OnHelloAsync(HubConnection conn, NnMsg msg)
    {
        var clientId = (msg.ClientId ?? "").Trim();
        if (clientId.Length == 0)
        {
            await conn.SendJsonAsync(new { t = "error", code = "bad_hello", msg = "缺少 clientId" });
            return;
        }
        if (clientId.Length > 64) clientId = clientId[..64];
        conn.ClientId = clientId;
        conn.Name = (msg.Name ?? "").Trim();
        conn.PublicUdp = msg.PublicUdp ?? "";
        conn.LocalUdp = msg.LocalUdp ?? "";

        // 同一 clientId 重连：替换旧连接，保留房间成员身份与虚拟 IP
        if (Conns.TryGetValue(clientId, out var old) && old != conn)
        {
            var member = old.Member;
            if (member != null)
            {
                conn.Member = member;
                member.Conn = conn;
            }
            try { await old.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); } catch { }
        }
        Conns[clientId] = conn;

        await conn.SendJsonAsync(new
        {
            t = "welcome",
            server = "NovaNet Hub",
            version = "1.0.0",
            protocol = NnConst.Version,
            myMemberId = conn.Member?.MemberId ?? "",
        });

        if (conn.Member != null)
        {
            // 回房：重发房间快照，并让其他成员重新打洞
            await ResendRoomAsync(conn);
            var room = conn.Member.Conn == conn ? FindRoomOf(conn.Member) : null;
            if (room != null)
                foreach (var other in room.Members)
                    if (other != conn.Member)
                        await SendAsync(other.Conn, new { t = "punch_req", from = RoomManager.RoomSnapshot.ToDto(conn.Member) });
        }
    }

    private static async Task OnCreateAsync(HubConnection conn, NnMsg msg)
    {
        try
        {
            if (conn.Member != null) throw new RoomException("已在一个房间中，请先离开");
            var snap = Rooms.CreateRoom(conn, msg.RoomName ?? "", msg.Password);
            if (snap == null) return;
            await conn.SendJsonAsync(snap.ToPayload());
        }
        catch (RoomException e) { await conn.SendJsonAsync(new { t = "error", code = "create", msg = e.Message }); }
    }

    private static async Task OnJoinAsync(HubConnection conn, NnMsg msg)
    {
        try
        {
            if (conn.Member != null) throw new RoomException("已在一个房间中，请先离开");
            var snap = Rooms.JoinRoom(conn, msg.Code ?? "", msg.Password);
            if (snap == null) return;
            await conn.SendJsonAsync(snap.ToPayload());
        }
        catch (RoomException e) { await conn.SendJsonAsync(new { t = "error", code = "join", msg = e.Message }); }
    }

    private static async Task OnKickAsync(HubConnection conn, NnMsg msg)
    {
        try
        {
            Rooms.Kick(conn, msg.MemberId ?? "");
            await ResendRoomAsync(conn);
        }
        catch (RoomException e) { await conn.SendJsonAsync(new { t = "error", code = "kick", msg = e.Message }); }
    }

    private static Task OnRelayAsync(HubConnection conn, NnMsg msg)
    {
        if (string.IsNullOrEmpty(msg.DataB64)) return Task.CompletedTask;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(msg.DataB64); } catch { return Task.CompletedTask; }
        try
        {
            var frame = NnFrame.Decode(bytes);
            Rooms.Relay(conn, bytes, frame.To);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static async Task ResendRoomAsync(HubConnection conn)
    {
        var member = conn.Member;
        if (member == null) return;
        var room = FindRoomOf(member);
        if (room == null) return;
        await conn.SendJsonAsync(new RoomManager.RoomSnapshot(room, member, member.MemberId).ToPayload());
    }

    private static Room? FindRoomOf(MemberRecord m)
        => Rooms.FindRoom(m);
}
