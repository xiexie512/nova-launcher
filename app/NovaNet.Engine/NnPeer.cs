using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace NovaNet.Engine;

/// <summary>到单个房间成员的隧道状态：路径（P2P/中继）、UDP 对端、活动流。</summary>
public sealed class NnPeer
{
    public NnMember Member { get; }
    public NnEngine Engine { get; }

    public bool IsP2P;
    public bool RelayReady;
    public IPEndPoint? RemoteUdp;
    public DateTime LastActiveUtc = DateTime.UtcNow;

    /// <summary>流内发送序号池（每条流独立，发起方分配）。</summary>
    public uint NextStreamId;

    public ConcurrentDictionary<uint, NnStream> Streams { get; } = new();

    private CancellationTokenSource? _punchCts;
    public CancellationToken PunchCancel { get; private set; }

    public NnPeer(NnEngine engine, NnMember member)
    {
        Engine = engine;
        Member = member;
        _punchCts = new CancellationTokenSource();
        PunchCancel = _punchCts.Token;
    }

    private int _punchStarted;

    /// <summary>打洞任务防重入：返回 true 表示本线程负责执行。</summary>
    public bool TryStartPunch() => Interlocked.Exchange(ref _punchStarted, 1) == 0;

    public string PathText => IsP2P ? "P2P 直连" : RelayReady ? "中继转发" : "连接中";

    public void CancelPunch()
    {
        try { _punchCts?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        CancelPunch();
        foreach (var s in Streams.Values) _ = s.DisposeAsync();
        Streams.Clear();
    }
}
