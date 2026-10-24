using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly Queue<double> _cpu = new();
    private readonly Queue<double> _mem = new();
    private const int MaxPoints = 60;

    private double _cpuNow;
    private double _memNow;
    private int _threads;
    private bool _running;

    public double CpuNow { get => _cpuNow; set => SetProperty(ref _cpuNow, value); }
    public double MemNow { get => _memNow; set => SetProperty(ref _memNow, value); }
    public int Threads { get => _threads; set => SetProperty(ref _threads, value); }
    public bool Running { get => _running; set => SetProperty(ref _running, value); }

    public event Action? ChartUpdated;

    public MonitorViewModel()
    {
        MonitorService.Sample += s =>
        {
            Running = s.Running;
            CpuNow = s.CpuPercent;
            MemNow = s.MemMb;
            Threads = s.Threads;
            Enqueue(_cpu, s.CpuPercent);
            Enqueue(_mem, s.MemMb);
            ChartUpdated?.Invoke();
        };
    }

    private static void Enqueue(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > MaxPoints) q.Dequeue();
    }

    public IReadOnlyList<double> CpuSeries => _cpu.ToArray();
    public IReadOnlyList<double> MemSeries => _mem.ToArray();

    [RelayCommand]
    private void Stop() => AppServices.Launcher.Stop();
}
