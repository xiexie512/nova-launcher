using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;

namespace NovaNet.Engine;

/// <summary>
/// 到某个对端成员的隧道流。一个流对应一条端到端 TCP 连接：
/// 发起方（玩家）本地 MC 连接 → 隧道 → 对端（房主）本机游戏端口。
/// 每个流有两类队列：Out = 待发往对端的数据（限 64 块），In = 对端发来、待写入本地 TCP 的数据。
/// </summary>
public sealed class NnStream : IAsyncDisposable
{
    public const int ChunkSize = 32 * 1024;
    public const int QueueCapacity = 64;

    public uint Sid { get; }
    public NnPeer Peer { get; }
    public TcpClient? Tcp { get; private set; }
    private NetworkStream? _tcpStream;
    private bool _tcpConnecting;

    public Channel<byte[]> Out { get; } = Channel.CreateBounded<byte[]>(QueueCapacity);
    public Channel<byte[]> In { get; } = Channel.CreateBounded<byte[]>(QueueCapacity);

    /// <summary>流内发送序号（每次 DATA 自增，接收方按此排序）。</summary>
    public uint OutSeq { get; private set; }

    /// <summary>已按序投递的最大流入序号（uint.MaxValue 表示尚未投递过任何块）。</summary>
    public uint LastInSeq = uint.MaxValue;
    private readonly Dictionary<uint, byte[]> _inBuf = new();
    private long _dropped;

    public bool Closing { get; private set; }
    public long Dropped => Interlocked.Read(ref _dropped);
    private int _runStarted;

    /// <summary>保证每条流只启动一轮泵任务（发往对端 / 写本地 TCP / 读本地 TCP）。</summary>
    public void EnsureRunning()
    {
        if (Interlocked.Exchange(ref _runStarted, 1) == 1) return;
        Peer.Engine.StartStreamLoops(this);
    }

    public NnStream(uint sid, NnPeer peer) => (Sid, Peer) = (sid, peer);

    public uint NextSeq() => OutSeq++;

    /// <summary>接入本地 TCP（发起方：玩家 MC 连接；接收方：首次收到数据时连本机游戏端口）。</summary>
    public void AttachTcp(TcpClient tcp)
    {
        Tcp = tcp;
        _tcpStream = tcp.GetStream();
    }

    public NetworkStream? Stream => _tcpStream;

    public async Task<NetworkStream?> EnsureTcpAsync(string host, int port, CancellationToken ct)
    {
        if (_tcpStream != null) return _tcpStream;
        if (_tcpConnecting) return null; // 由另一任务建立
        _tcpConnecting = true;
        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            AttachTcp(tcp);
            return _tcpStream;
        }
        catch
        {
            try { Tcp?.Dispose(); } catch { }
            Tcp = null; _tcpStream = null;
            return null;
        }
        finally { _tcpConnecting = false; }
    }

    /// <summary>
    /// 接收方按序投递：处理乱序（窗口 64）与重复，把就绪块写入 In 队列。
    /// </summary>
    public void Deliver(uint sseq, byte[] chunk)
    {
        if (Closing) return;
        lock (_inBuf)
        {
            // 下一个期望序号：尚未投递任何块时为 0
            uint next = LastInSeq == uint.MaxValue ? 0u : LastInSeq + 1u;
            if (sseq < next)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            if (sseq == next)
            {
                LastInSeq = sseq;
                TryWrite(chunk);
                // 顺带吐出积压的连续块
                while (_inBuf.TryGetValue(LastInSeq + 1u, out var nxt))
                {
                    _inBuf.Remove(LastInSeq + 1u);
                    LastInSeq++;
                    if (!TryWrite(nxt)) break;
                }
                return;
            }
            // 乱序块：入缓冲，超出窗口则丢弃最旧
            if (_inBuf.Count >= NnConst.StreamReorderWindow)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _inBuf[sseq] = chunk;
        }
    }

    private bool TryWrite(byte[] chunk)
    {
        if (Closing) { Interlocked.Increment(ref _dropped); return false; }
        if (In.Writer.TryWrite(chunk)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void MarkClosing()
    {
        Closing = true;
        Out.Writer.TryComplete();
        In.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        MarkClosing();
        try { _tcpStream?.Dispose(); } catch { }
        try { Tcp?.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
