using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace NovaNet.Engine;

/// <summary>
/// NovaNet 客户端引擎：连接自研 Hub（WS 信令 + WS 中继回退）、UDP 打洞直连（P2P）、
/// 端到端 AES-GCM 加密隧道、本地 TCP 端口映射（127.0.0.1:200xx → 对端游戏端口）。
/// 协议定义见 NnFrame.cs，与服务端共用同一份实现。
/// </summary>
public sealed class NnEngine : IAsyncDisposable
{
    private readonly NovaNetConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private long _sendSeq;

    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
    private UdpClient? _udp;
    private IPEndPoint? _hubUdpEp;

    private readonly Dictionary<string, NnPeer> _peers = new();
    private readonly Dictionary<string, TcpListener> _listeners = new();
    private NnCrypto? _crypto;

    private volatile NnState _state;
    private string _stateMsg = "";
    private NnRoom? _room;
    private string _myMemberId = "";
    private readonly List<NnMember> _members = new();

    private Task? _connectTask;
    private Task? _udpTask;
    private Task? _keepAliveTask;

    private sealed class PendingOp
    {
        public required string T;
        public required TaskCompletionSource<NnMsg> Tcs;
    }
    private readonly ConcurrentDictionary<string, PendingOp> _pending = new();

    // ================= 事件 =================

    public event Action<NnState, string>? StateChanged;
    public event Action<NnRoom?>? RoomChanged;
    public event Action<IReadOnlyList<NnMember>>? MembersChanged;
    public event Action<string, string>? PeerPathChanged; // memberId, pathText
    public event Action<string>? Log;
    public event Action<string>? Kicked;

    // ================= 只读状态 =================

    public NovaNetConfig Config => _cfg;
    public NnState State => _state;
    public string StateMessage => _stateMsg;
    public NnRoom? Room => _room;
    public string MyMemberId => _myMemberId;
    public string MyVirtualIp => _room?.MyVirtualIp ?? "";

    public IReadOnlyList<NnMember> Members
    {
        get { lock (_stateLock) return _members.ToList(); }
    }

    public int? ConnectPortFor(string memberId)
    {
        lock (_stateLock) return _members.FirstOrDefault(m => m.MemberId == memberId)?.LocalPort;
    }

    public NnEngine(NovaNetConfig cfg)
    {
        _cfg = cfg;
        _sendSeq = (uint)Random.Shared.NextInt64(1, int.MaxValue);
    }

    // ================= 生命周期 =================

    public void Start()
    {
        lock (_stateLock)
        {
            if (_state != NnState.Stopped) return;
            _state = NnState.Connecting;
        }
        _ = InitUdpAsync();
        _connectTask = Task.Run(ConnectLoopAsync);
        _keepAliveTask = Task.Run(KeepAliveLoopAsync);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        CloseWs();
        _udp?.Dispose();
        SetState(NnState.Stopped, "已停止");
        try { if (_connectTask != null) await _connectTask; } catch { }
        CleanupRoom();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void SetState(NnState s, string msg)
    {
        NnState old;
        lock (_stateLock) { old = _state; _state = s; _stateMsg = msg; }
        if (old != s) StateChanged?.Invoke(s, msg);
    }

    // ================= UDP：绑定 + STUN 探测公网端点 =================

    private async Task InitUdpAsync()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            _udp = udp;
            _hubUdpEp = HubUdpEndpoint();

            // STUN：向 Hub UDP 端口发探测，Hub 回显观察到的公网端点
            var stun = Encoding.ASCII.GetBytes("STUN" + (_cfg.ClientId.Length >= 16 ? _cfg.ClientId[..16] : _cfg.ClientId.PadRight(16, ' ')));
            for (var i = 0; i < 3 && !_cts.IsCancellationRequested; i++)
            {
                try
                {
                    await udp.SendAsync(stun, _hubUdpEp, _cts.Token);
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    t.CancelAfter(2000);
                    var res = await udp.ReceiveAsync(t.Token);
                    var reply = Encoding.UTF8.GetString(res.Buffer);
                    if (NnNet.TryParse(reply, out _, out _))
                    {
                        _cfg.PublicUdp = reply;
                        break;
                    }
                }
                catch { /* 重试 */ }
            }
            // 首次发送后套接字已绑定，此时读本地端点才是真实端口
            var local = GetLocalLanIp();
            var localPort = ((IPEndPoint?)udp.Client.LocalEndPoint)?.Port ?? 0;
            _cfg.LocalUdp = localPort > 0 ? $"{local}:{localPort}" : "";
            Log?.Invoke($"公网 UDP 端点 {_cfg.PublicUdp} · 本地 UDP {_cfg.LocalUdp}");
            if (string.IsNullOrEmpty(_cfg.PublicUdp))
                Log?.Invoke("警告：无法探测公网 UDP 端点（NAT 打洞将受限，只能走中继）");

            _udpTask = Task.Run(UdpReceiveLoopAsync);
        }
        catch (Exception e)
        {
            Log?.Invoke("UDP 初始化失败: " + e.Message);
        }
    }

    private static string GetLocalLanIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address) &&
                        !ua.Address.ToString().StartsWith("169.254."))
                        return ua.Address.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private IPEndPoint HubUdpEndpoint()
    {
        var uri = NormalizeUri(_cfg.ServerUri);
        var port = uri.Port > 0 ? uri.Port : NnConst.DefaultPort;
        if (IPAddress.TryParse(uri.Host, out var ip)) return new IPEndPoint(ip, port);
        var ips = Dns.GetHostAddresses(uri.Host);
        return new IPEndPoint(ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? ips[0], port);
    }

    private static Uri NormalizeUri(string s)
    {
        if (!s.Contains("://")) s = "ws://" + s;
        var uri = new Uri(s);
        // Hub 的 WS 端点固定为 /ws：默认路径（/ 或空）时自动补上
        if (uri.AbsolutePath is "/" or "")
        {
            var ub = new UriBuilder(uri) { Path = "/ws" };
            return ub.Uri;
        }
        return uri;
    }

    // ================= WS 连接循环（自动重连） =================

    private async Task ConnectLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync();
            }
            catch (Exception e)
            {
                Log?.Invoke("连接失败: " + e.Message);
            }
            if (_cts.IsCancellationRequested) break;
            CloseWs();
            SetState(NnState.Reconnecting, "连接断开，5 秒后自动重连");
            try { await Task.Delay(5000, _cts.Token); } catch { break; }
        }
    }

    private async Task ConnectOnceAsync()
    {
        var uri = NormalizeUri(_cfg.ServerUri);
        SetState(NnState.Connecting, $"正在连接 Hub {uri.Host}:{uri.Port}…");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        ct.CancelAfter(15000);
        await ws.ConnectAsync(uri, ct.Token);
        _ws = ws;
        SetState(NnState.Online, $"已连接 Hub {uri.Host}:{uri.Port}");

        await SendJsonAsync(new NnMsg
        {
            T = "hello",
            ClientId = _cfg.ClientId,
            Name = _cfg.Name,
            Version = "1.0.0",
            PublicUdp = _cfg.PublicUdp,
            LocalUdp = _cfg.LocalUdp,
        });

        // 断线重连后自动回到上次房间
        if (!string.IsNullOrEmpty(_cfg.AutoJoinCode))
        {
            try
            {
                await JoinRoomAsync(_cfg.AutoJoinCode, _cfg.AutoJoinPassword);
            }
            catch (Exception e)
            {
                Log?.Invoke("自动回房失败: " + e.Message);
            }
        }

        var buf = new byte[8192];
        var text = new MemoryStream();
        var binary = new MemoryStream();
        while (!_cts.IsCancellationRequested)
        {
            var seg = new ArraySegment<byte>(buf);
            var res = await ws.ReceiveAsync(seg, _cts.Token);
            if (res.MessageType == WebSocketMessageType.Close) break;
            if (res.MessageType == WebSocketMessageType.Binary)
            {
                binary.Write(buf, 0, res.Count);
                if (res.EndOfMessage)
                {
                    var frameBytes = binary.ToArray();
                    binary.SetLength(0);
                    try
                    {
                        var frame = NnFrame.Decode(frameBytes);
                        await OnPeerFrameAsync(frame, fromRelay: true);
                    }
                    catch (Exception e) { Log?.Invoke("中继帧解析失败: " + e.Message); }
                }
                continue;
            }
            text.Write(buf, 0, res.Count);
            if (res.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(text.ToArray());
                text.SetLength(0);
                await DispatchAsync(json);
            }
        }
        throw new Exception("WebSocket 已关闭");
    }

    // ================= 收帧统一入口（UDP 与 WS 中继共用） =================

    private async Task OnPeerFrameAsync(NnFrame frame, bool fromRelay)
    {
        if (string.IsNullOrEmpty(frame.From) || frame.From == _myMemberId) return;
        NnPeer? peer;
        lock (_stateLock) _peers.TryGetValue(frame.From, out peer);
        if (peer == null) return;
        peer.LastActiveUtc = DateTime.UtcNow;

        switch (frame.Type)
        {
            case NnType.Connect:
            case NnType.KeepAlive:
                // 只有 UDP 收到的帧才能证明 P2P 可达；中继帧不能把路径标成 P2P
                if (!fromRelay) MarkP2P(peer);
                break;
            case NnType.Data:
                if (!fromRelay) MarkP2P(peer);
                HandleData(peer, frame);
                break;
            case NnType.Close:
                await HandleCloseAsync(peer, frame);
                break;
        }
    }

    private void MarkP2P(NnPeer peer)
    {
        if (peer.IsP2P) return;
        peer.IsP2P = true;
        peer.CancelPunch();
        peer.Member.PathText = "P2P 直连";
        PeerPathChanged?.Invoke(peer.Member.MemberId, peer.Member.PathText);
        Log?.Invoke($"与 {peer.Member.Name}({peer.Member.VirtualIp}) 建立 P2P 直连");
    }

    // ================= 流数据处理 =================

    private void HandleData(NnPeer peer, NnFrame frame)
    {
        byte[] plain;
        try
        {
            plain = frame.Encrypted && _crypto != null
                ? _crypto.Decrypt(frame.From, frame.Seq, frame.Payload)
                : frame.Payload;
        }
        catch (Exception e) { Log?.Invoke($"解密失败 from={frame.From} seq={frame.Seq}: {e.Message}"); return; }
        if (plain.Length < 8) return;
        var sid = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(0, 4));
        var sseq = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4, 4));
        var chunk = plain[8..];

        var stream = peer.Streams.GetOrAdd(sid, s => new NnStream(s, peer));
        stream.Deliver(sseq, chunk);
        stream.EnsureRunning();
    }

    private async Task HandleCloseAsync(NnPeer peer, NnFrame frame)
    {
        byte[] plain;
        try
        {
            plain = frame.Encrypted && _crypto != null
                ? _crypto.Decrypt(frame.From, frame.Seq, frame.Payload)
                : frame.Payload;
        }
        catch { return; }
        if (plain.Length < 4) return;
        var sid = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(0, 4));
        if (peer.Streams.TryRemove(sid, out var stream))
            await stream.DisposeAsync();
    }

    /// <summary>每条流只启动一轮泵任务（发往对端 / 写入本地 TCP / 读取本地 TCP）。</summary>
    internal void StartStreamLoops(NnStream stream)
    {
        _ = Task.Run(() => StreamSendLoopAsync(stream));
        _ = Task.Run(() => StreamInLoopAsync(stream));
        if (stream.Stream != null)
            _ = Task.Run(() => StreamLocalReadLoopAsync(stream));
        else
            _ = Task.Run(() => StreamReceiverBootstrapAsync(stream));
    }

    /// <summary>把本地 TCP 读到的数据经隧道发往对端（发起方：玩家 MC 连接；接收方：游戏服回包）。</summary>
    private async Task StreamSendLoopAsync(NnStream stream)
    {
        try
        {
            await foreach (var chunk in stream.Out.Reader.ReadAllAsync(_cts.Token))
            {
                var plain = new byte[8 + chunk.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(0, 4), stream.Sid);
                BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4, 4), stream.NextSeq());
                chunk.CopyTo(plain, 8);
                await SendFrameAsync(stream.Peer, NnType.Data, plain);
            }
        }
        catch { }
    }

    /// <summary>把对端发来的数据写入本地 TCP（无连接时按接收方身份连本机游戏端口）。</summary>
    private async Task StreamInLoopAsync(NnStream stream)
    {
        try
        {
            await foreach (var chunk in stream.In.Reader.ReadAllAsync(_cts.Token))
            {
                if (stream.Stream == null)
                {
                    // 接收方首次数据 → 连本机游戏端口
                    var ns = await stream.EnsureTcpAsync("127.0.0.1", _cfg.LocalGamePort, _cts.Token);
                    if (ns == null)
                    {
                        Log?.Invoke($"无法连接本机游戏端口 {_cfg.LocalGamePort}（游戏未开启局域网？）");
                        await SendCloseAsync(stream.Peer, stream.Sid, stream.OutSeq);
                        break;
                    }
                    _ = Task.Run(() => StreamLocalReadLoopAsync(stream));
                }
                await stream.Stream!.WriteAsync(chunk, _cts.Token);
            }
        }
        catch { }
        finally { await CloseStreamIfIdleAsync(stream); }
    }

    /// <summary>接收方兜底：等第一条数据才建连，防止空转。</summary>
    private async Task StreamReceiverBootstrapAsync(NnStream stream)
    {
        // StreamInLoop 负责建连；本任务只兜底超时清理
        try { await Task.Delay(TimeSpan.FromMinutes(2), _cts.Token); } catch { }
        await CloseStreamIfIdleAsync(stream);
    }

    /// <summary>读取本地 TCP → 写入 Out 队列（带背压）。EOF 时发 CLOSE。</summary>
    private async Task StreamLocalReadLoopAsync(NnStream stream)
    {
        var buf = new byte[NnStream.ChunkSize];
        try
        {
            while (!_cts.IsCancellationRequested && !stream.Closing)
            {
                if (stream.Stream == null) { await Task.Delay(100, _cts.Token); continue; }
                var n = await stream.Stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                var outChunk = new byte[n];
                Buffer.BlockCopy(buf, 0, outChunk, 0, n);
                await stream.Out.Writer.WriteAsync(outChunk, _cts.Token);
            }
        }
        catch { }
        finally
        {
            if (!stream.Closing)
            {
                await SendCloseAsync(stream.Peer, stream.Sid, stream.OutSeq);
                await CloseStreamIfIdleAsync(stream);
            }
        }
    }

    private async Task CloseStreamIfIdleAsync(NnStream stream)
    {
        if (stream.Closing) return;
        stream.MarkClosing();
        await stream.DisposeAsync();
        stream.Peer.Streams.TryRemove(stream.Sid, out _);
    }

    private async Task SendCloseAsync(NnPeer peer, uint sid, uint sseq)
    {
        var plain = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(0, 4), sid);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4, 4), sseq);
        await SendFrameAsync(peer, NnType.Close, plain);
    }

    // ================= UDP 收包循环 =================

    private async Task UdpReceiveLoopAsync()
    {
        var buf = new byte[NnConst.HeaderSize + NnConst.MaxPayload];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var res = await _udp!.ReceiveAsync(_cts.Token);
                var frame = NnFrame.Decode(res.Buffer);
                if (frame.From == _myMemberId) continue;
                // CONNECT 打洞包 To 为空，只要来自房间成员就处理；DATA/CLOSE 才校验 To
                if (frame.To.Length > 0 && frame.To != _myMemberId) continue;
                await OnPeerFrameAsync(frame, fromRelay: false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* 忽略坏包 */ }
        }
    }

    // ================= 发送路径 =================

    private async Task SendFrameAsync(NnPeer peer, NnType type, byte[] plain)
    {
        NnRoom? room;
        lock (_stateLock) room = _room;
        if (room == null) return;

        // 等待路径就绪（P2P 或中继），最多 10 秒
        for (var i = 0; i < 50 && !peer.IsP2P && !peer.RelayReady && !_cts.IsCancellationRequested; i++)
            await Task.Delay(200, _cts.Token);
        if (_cts.IsCancellationRequested) return;

        var seq = (uint)Interlocked.Increment(ref _sendSeq);
        byte[] payload = plain;
        var encrypted = false;
        if (type == NnType.Data && _crypto != null)
        {
            payload = _crypto.Encrypt(_myMemberId, seq, plain);
            encrypted = true;
        }
        var frame = new NnFrame(type, room.Id, _myMemberId, peer.Member.MemberId, encrypted, seq, payload);

        if (peer.IsP2P)
        {
            try
            {
                peer.RemoteUdp ??= NnNet.ToEndPoint(peer.Member.PublicUdp);
                await _udp!.SendAsync(frame.Encode(), peer.RemoteUdp, _cts.Token);
            }
            catch { }
        }
        else if (peer.RelayReady)
        {
            await SendJsonAsync(new NnMsg { T = "relay", Target = peer.Member.MemberId, DataB64 = Convert.ToBase64String(frame.Encode()) });
        }
    }

    private async Task SendJsonAsync(NnMsg msg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(msg, NnJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _wsSendLock.WaitAsync(_cts.Token);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        finally { _wsSendLock.Release(); }
    }

    // ================= 信令收包分派 =================

    private async Task DispatchAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : "";
            switch (t)
            {
                case "welcome":
                    if (root.TryGetProperty("myMemberId", out var mid) && mid.ValueKind == JsonValueKind.String)
                    { lock (_stateLock) _myMemberId = mid.GetString() ?? ""; }
                    break;
                case "room":
                    ApplyRoom(root);
                    CompleteRoomPending();
                    break;
                case "member":
                    ApplyMember(root);
                    break;
                case "gone":
                    RemoveMember(root);
                    break;
                case "kicked":
                    Kicked?.Invoke("你已被房主移出房间");
                    LeaveRoomInternal("你已被房主移出房间");
                    break;
                case "punch_req":
                    await OnPunchRequestAsync(root);
                    break;
                case "punch_resp":
                    await OnPunchResponseAsync(root);
                    break;
                case "error":
                    FailPending(GetStr(root, "msg"));
                    Log?.Invoke($"服务端错误: {GetStr(root, "msg")}");
                    break;
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("信令解析失败: " + e.Message);
        }
    }

    private void ApplyRoom(JsonElement root)
    {
        NnRoom room;
        List<NnMember> members;
        if (root.TryGetProperty("room", out var r) && root.TryGetProperty("members", out var ms))
        {
            // 先确定自己的 memberId，再过滤成员列表（否则会把自己也当成对端）
            if (root.TryGetProperty("myMemberId", out var mid) && mid.ValueKind == JsonValueKind.String)
                lock (_stateLock) _myMemberId = mid.GetString() ?? "";
            var d = JsonSerializer.Deserialize<NnRoomDto>(r.GetRawText(), NnJson.Options)!;
            room = new NnRoom
            {
                Id = d.Id, Code = d.Code, Name = d.Name,
                MyVirtualIp = d.MyVirtualIp, IsOwner = d.IsOwner, HasPassword = d.HasPassword,
            };
            members = new List<NnMember>();
            foreach (var el in ms.EnumerateArray())
            {
                var dm = JsonSerializer.Deserialize<NnMemberDto>(el.GetRawText(), NnJson.Options)!;
                if (dm.MemberId == MyMemberId) continue;
                members.Add(new NnMember
                {
                    MemberId = dm.MemberId, Name = dm.Name, VirtualIp = dm.VirtualIp,
                    PublicUdp = dm.PublicUdp, LocalUdp = dm.LocalUdp,
                    Online = dm.Online, IsOwner = dm.IsOwner, JoinedAt = dm.JoinedAt,
                });
            }
        }
        else return;

        lock (_stateLock)
        {
            _room = room;
            _crypto = room.HasPassword && !string.IsNullOrEmpty(_cfg.AutoJoinPassword)
                ? new NnCrypto(room.Code, _cfg.AutoJoinPassword!)
                : null;
            _members.Clear();
            _members.AddRange(members);
        }
        ReconcilePeers();
        RoomChanged?.Invoke(room);
        MembersChanged?.Invoke(Members);
    }

    private void ApplyMember(JsonElement root)
    {
        if (!root.TryGetProperty("member", out var el)) return;
        var d = JsonSerializer.Deserialize<NnMemberDto>(el.GetRawText(), NnJson.Options)!;
        if (d.MemberId == MyMemberId) return;
        lock (_stateLock)
        {
            var m = _members.FirstOrDefault(x => x.MemberId == d.MemberId);
            if (m == null)
            {
                _members.Add(new NnMember
                {
                    MemberId = d.MemberId, Name = d.Name, VirtualIp = d.VirtualIp,
                    PublicUdp = d.PublicUdp, LocalUdp = d.LocalUdp,
                    Online = d.Online, IsOwner = d.IsOwner, JoinedAt = d.JoinedAt,
                });
            }
            else
            {
                m.Name = d.Name; m.VirtualIp = d.VirtualIp; m.PublicUdp = d.PublicUdp;
                m.LocalUdp = d.LocalUdp; m.Online = d.Online; m.IsOwner = d.IsOwner;
            }
        }
        ReconcilePeers();
        MembersChanged?.Invoke(Members);
    }

    private void RemoveMember(JsonElement root)
    {
        var id = GetStr(root, "memberId");
        lock (_stateLock)
        {
            _members.RemoveAll(m => m.MemberId == id);
            if (_peers.TryGetValue(id, out var peer)) { peer.Dispose(); _peers.Remove(id); }
            if (_listeners.TryGetValue(id, out var li)) { try { li.Stop(); } catch { } _listeners.Remove(id); }
        }
        MembersChanged?.Invoke(Members);
    }

    // ================= 房间成员对账 =================

    private void ReconcilePeers()
    {
        lock (_stateLock)
        {
            foreach (var id in _peers.Keys.ToList())
            {
                if (!_members.Any(m => m.MemberId == id))
                {
                    _peers[id].Dispose();
                    _peers.Remove(id);
                    if (_listeners.TryGetValue(id, out var li)) { try { li.Stop(); } catch { } _listeners.Remove(id); }
                }
            }
            foreach (var m in _members)
            {
                if (m.MemberId == MyMemberId) continue;
                if (!_peers.TryGetValue(m.MemberId, out var peer))
                {
                    peer = new NnPeer(this, m);
                    _peers[m.MemberId] = peer;
                    if (peer.TryStartPunch()) _ = Task.Run(() => PunchAsync(peer));
                }
                if (!_listeners.ContainsKey(m.MemberId) && !string.IsNullOrEmpty(m.VirtualIp))
                    StartListener(m, peer);
            }
        }
    }

    private void StartListener(NnMember m, NnPeer peer)
    {
        var baseOctet = TryLastOctet(m.VirtualIp);
        var port = NnConst.LocalBasePort + Math.Max(2, baseOctet);
        for (var i = 0; i < 20; i++)
        {
            var candidate = port + i;
            if (_listeners.Values.Any(l => ((IPEndPoint?)l.LocalEndpoint)?.Port == candidate)) continue;
            try
            {
                var li = new TcpListener(IPAddress.Loopback, candidate);
                li.Start();
                lock (_stateLock) _listeners[m.MemberId] = li;
                m.LocalPort = candidate;
                m.PathText = peer.PathText;
                _ = Task.Run(() => ListenerLoopAsync(li, m.MemberId));
                Log?.Invoke($"已开启 {m.Name} 的联机映射 127.0.0.1:{candidate} → {m.VirtualIp}");
                return;
            }
            catch { /* 端口占用，顺延 */ }
        }
        m.LocalPort = 0;
        Log?.Invoke($"无法为 {m.Name} 分配映射端口");
    }

    private static int TryLastOctet(string ip)
    {
        var i = ip.LastIndexOf('.');
        return int.TryParse(i >= 0 ? ip[(i + 1)..] : "0", out var o) ? o : 0;
    }

    private async Task ListenerLoopAsync(TcpListener li, string memberId)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await li.AcceptTcpClientAsync(_cts.Token);
                NnPeer? peer;
                lock (_stateLock) _peers.TryGetValue(memberId, out peer);
                if (peer == null) { tcp.Dispose(); continue; }
                var sid = peer.NextStreamId++;
                var stream = new NnStream(sid, peer);
                stream.AttachTcp(tcp);
                peer.Streams[sid] = stream;
                stream.EnsureRunning();
            }
        }
        catch { }
        finally { try { li.Stop(); } catch { } }
    }

    // ================= UDP 打洞 =================

    private async Task PunchAsync(NnPeer peer)
    {
        if (_cfg.DisableP2P)
        {
            peer.RelayReady = true;
            peer.Member.PathText = "中继转发";
            PeerPathChanged?.Invoke(peer.Member.MemberId, peer.Member.PathText);
            return;
        }
        try
        {
            await SendJsonAsync(new NnMsg { T = "punch", Target = peer.Member.MemberId });
            for (var i = 0; i < 10 && !peer.IsP2P && !peer.RelayReady && !_cts.IsCancellationRequested; i++)
            {
                await SendConnectAsync(peer.Member.PublicUdp);
                await SendConnectAsync(peer.Member.LocalUdp);
                await Task.Delay(400, peer.PunchCancel);
            }
            if (!peer.IsP2P && !_cts.IsCancellationRequested)
            {
                peer.RelayReady = true;
                peer.Member.PathText = "中继转发";
                PeerPathChanged?.Invoke(peer.Member.MemberId, peer.Member.PathText);
                Log?.Invoke($"与 {peer.Member.Name} 打洞失败，回退中继转发");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Invoke($"打洞异常({peer.Member.Name}): {e.Message}"); }
    }

    private async Task SendConnectAsync(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return;
        NnRoom? room;
        lock (_stateLock) room = _room;
        if (room == null || _myMemberId.Length == 0) return;
        try
        {
            var frame = new NnFrame(NnType.Connect, room.Id, _myMemberId, "", false, (uint)Interlocked.Increment(ref _sendSeq), Array.Empty<byte>());
            await _udp!.SendAsync(frame.Encode(), NnNet.ToEndPoint(endpoint), _cts.Token);
        }
        catch { }
    }

    private async Task OnPunchRequestAsync(JsonElement root)
    {
        var from = ParseFrom(root);
        if (from == null) return;
        lock (_stateLock)
        {
            if (!_peers.TryGetValue(from.MemberId, out var p))
            {
                var m = _members.FirstOrDefault(x => x.MemberId == from.MemberId);
                if (m == null) return;
                p = new NnPeer(this, m);
                _peers[m.MemberId] = p;
                if (!_listeners.ContainsKey(m.MemberId)) StartListener(m, p);
            }
            if (p.TryStartPunch()) _ = Task.Run(() => PunchAsync(p));
        }
        await SendJsonAsync(new NnMsg { T = "punch_ok", Target = from.MemberId });
    }

    private Task OnPunchResponseAsync(JsonElement root)
    {
        var from = ParseFrom(root);
        if (from == null) return Task.CompletedTask;
        lock (_stateLock)
        {
            if (!_peers.TryGetValue(from.MemberId, out var p))
            {
                var m = _members.FirstOrDefault(x => x.MemberId == from.MemberId);
                if (m == null) return Task.CompletedTask;
                p = new NnPeer(this, m);
                _peers[m.MemberId] = p;
                if (!_listeners.ContainsKey(m.MemberId)) StartListener(m, p);
            }
            if (p.TryStartPunch()) _ = Task.Run(() => PunchAsync(p));
        }
        return Task.CompletedTask;
    }

    private static NnMember? ParseFrom(JsonElement root)
    {
        if (!root.TryGetProperty("from", out var f)) return null;
        try
        {
            var d = JsonSerializer.Deserialize<NnMemberDto>(f.GetRawText(), NnJson.Options)!;
            return new NnMember { MemberId = d.MemberId, Name = d.Name, VirtualIp = d.VirtualIp, PublicUdp = d.PublicUdp, LocalUdp = d.LocalUdp };
        }
        catch { return null; }
    }

    // ================= P2P 保活 =================

    private async Task KeepAliveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(10000, _cts.Token); } catch { break; }
            NnRoom? room;
            NnPeer[] peers;
            lock (_stateLock) { room = _room; peers = _peers.Values.Where(p => p.IsP2P).ToArray(); }
            if (room == null) continue;
            foreach (var p in peers)
            {
                try
                {
                    var frame = new NnFrame(NnType.KeepAlive, room.Id, _myMemberId, p.Member.MemberId, false, (uint)Interlocked.Increment(ref _sendSeq), Array.Empty<byte>());
                    p.RemoteUdp ??= NnNet.ToEndPoint(p.Member.PublicUdp);
                    await _udp!.SendAsync(frame.Encode(), p.RemoteUdp, _cts.Token);
                }
                catch { }
            }
        }
    }

    // ================= 房间操作 API =================

    public async Task<string> CreateRoomAsync(string name, string? password = null)
    {
        // 先记录密码：room 消息可能在返回前就到达，加密需要它
        _cfg.AutoJoinPassword = password;
        var p = new PendingOp { T = "create", Tcs = new TaskCompletionSource<NnMsg>(TaskCreationOptions.RunContinuationsAsynchronously) };
        _pending[$"create:{Guid.NewGuid():N}"] = p;
        await SendJsonAsync(new NnMsg { T = "create", RoomName = name, Password = password });
        var res = await AwaitRoom(p);
        _cfg.AutoJoinCode = res.Code;
        return res.Code ?? "";
    }

    public async Task<string> JoinRoomAsync(string code, string? password = null)
    {
        _cfg.AutoJoinPassword = password;
        var p = new PendingOp { T = "join", Tcs = new TaskCompletionSource<NnMsg>(TaskCreationOptions.RunContinuationsAsynchronously) };
        _pending[$"join:{Guid.NewGuid():N}"] = p;
        await SendJsonAsync(new NnMsg { T = "join", Code = code.Trim().ToUpperInvariant(), Password = password });
        var res = await AwaitRoom(p);
        _cfg.AutoJoinCode = res.Code;
        return res.Code ?? "";
    }

    public async Task LeaveRoomAsync()
    {
        try { await SendJsonAsync(new NnMsg { T = "leave" }); } catch { }
        LeaveRoomInternal("已离开房间");
    }

    public async Task KickAsync(string memberId)
    {
        await SendJsonAsync(new NnMsg { T = "kick", MemberId = memberId });
    }

    private async Task<NnMsg> AwaitRoom(PendingOp p)
    {
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        ct.CancelAfter(10000);
        try
        {
            return await p.Tcs.Task.WaitAsync(ct.Token);
        }
        catch (TimeoutException)
        {
            throw new Exception("操作超时：Hub 未响应");
        }
        finally
        {
            var key = _pending.FirstOrDefault(kv => kv.Value == p).Key;
            if (key != null) _pending.TryRemove(key, out _);
        }
    }

    private void CompleteRoomPending()
    {
        NnRoom? room;
        lock (_stateLock) room = _room;
        foreach (var kv in _pending.ToList())
        {
            if (kv.Value.T is "create" or "join")
                kv.Value.Tcs.TrySetResult(new NnMsg { T = kv.Value.T, Code = room?.Code ?? "" });
        }
    }

    private void FailPending(string msg)
    {
        foreach (var kv in _pending.ToList())
        {
            if (kv.Value.T is "create" or "join")
                kv.Value.Tcs.TrySetException(new Exception(string.IsNullOrEmpty(msg) ? "操作失败" : msg));
        }
    }

    private void LeaveRoomInternal(string reason)
    {
        lock (_stateLock)
        {
            _room = null;
            _crypto = null;
            foreach (var p in _peers.Values) p.Dispose();
            _peers.Clear();
            foreach (var li in _listeners.Values) { try { li.Stop(); } catch { } }
            _listeners.Clear();
            _members.Clear();
        }
        RoomChanged?.Invoke(null);
        MembersChanged?.Invoke(Members);
        Log?.Invoke(reason);
    }

    private void CleanupRoom()
    {
        lock (_stateLock)
        {
            _room = null;
            _crypto = null;
            foreach (var p in _peers.Values) p.Dispose();
            _peers.Clear();
            foreach (var li in _listeners.Values) { try { li.Stop(); } catch { } }
            _listeners.Clear();
            _members.Clear();
        }
    }

    private static string GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void CloseWs()
    {
        try
        {
            var ws = _ws;
            if (ws != null)
            {
                _ws = null;
                if (ws.State == WebSocketState.Open)
                {
                    using var ct = new CancellationTokenSource(2000);
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct.Token).GetAwaiter().GetResult();
                }
                ws.Dispose();
            }
        }
        catch { }
    }
}
