using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NovaLauncher.Services;

public sealed class MonitorSample
{
    public double CpuPercent { get; init; }
    public double MemMb { get; init; }
    public int Threads { get; init; }
    public long Pid { get; init; }
    public bool Running { get; init; }
}

/// <summary>游戏运行监控：CPU / 内存 / 线程，每秒采样。</summary>
public static class MonitorService
{
    public static event Action<MonitorSample>? Sample;
    private static Process? _proc;
    private static CancellationTokenSource? _cts;
    private static DateTime _lastTime;
    private static TimeSpan _lastCpu;

    public static bool IsMonitoring => _proc != null;

    public static void Start(Process p)
    {
        Stop();
        _proc = p;
        _lastTime = DateTime.UtcNow;
        try { _lastCpu = p.TotalProcessorTime; } catch { _lastCpu = TimeSpan.Zero; }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoop(_cts.Token));
    }

    public static void StopIfRunning(int pid)
    {
        if (_proc != null && _proc.Id == pid) Stop();
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _proc = null;
    }

    private static async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var p = _proc;
            if (p == null || p.HasExited)
            {
                Sample?.Invoke(new MonitorSample { Running = false });
                Stop();
                return;
            }
            try
            {
                var now = DateTime.UtcNow;
                var cpu = p.TotalProcessorTime;
                var dt = (now - _lastTime).TotalSeconds;
                double pct = 0;
                if (dt > 0.2)
                {
                    var d = (cpu - _lastCpu).TotalSeconds / dt;
                    pct = d * 100.0 / Environment.ProcessorCount;
                    _lastTime = now;
                    _lastCpu = cpu;
                }
                var memMb = p.WorkingSet64 / 1024.0 / 1024.0;
                int threads;
                try { threads = p.Threads.Count; } catch { threads = 0; }
                Sample?.Invoke(new MonitorSample
                {
                    CpuPercent = Math.Clamp(pct, 0, 400),
                    MemMb = memMb,
                    Threads = threads,
                    Pid = p.Id,
                    Running = true,
                });
            }
            catch { /* 进程已退出等 */ }
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }
}
