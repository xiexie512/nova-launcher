using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class ConsoleViewModel : ObservableObject
{
    private string _filter = "";
    private bool _autoScroll = true;
    private string _diagnosticStatus = "";
    private string _lastAnalyzedVersion = "";

    public ObservableCollection<string> Lines { get; } = new();
    public ObservableCollection<string> Filtered { get; } = new();

    /// <summary>崩溃诊断结论（游戏退出后自动分析，也可手动触发）。</summary>
    public ObservableCollection<GameDiagnostics.Finding> Findings { get; } = new();

    public string Filter
    {
        get => _filter;
        set { SetProperty(ref _filter, value); ApplyFilter(); }
    }
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }
    public bool HasFindings => Findings.Count > 0;
    public string DiagnosticStatus { get => _diagnosticStatus; set => SetProperty(ref _diagnosticStatus, value); }

    public ConsoleViewModel()
    {
        foreach (var l in LogService.Lines) AddLine(l);
        LogService.LineAppended += AddLine;
        // 游戏退出后自动分析一次崩溃日志
        AppServices.Launcher.Stopped += () =>
        {
            var id = AppServices.Launcher.LastVersionId;
            if (!string.IsNullOrEmpty(id)) Analyze(id);
        };
    }

    private void Analyze(string versionId)
    {
        var findings = GameDiagnostics.Analyze(versionId);
        Findings.Clear();
        foreach (var f in findings) Findings.Add(f);
        _lastAnalyzedVersion = versionId;
        OnPropertyChanged(nameof(HasFindings));
        DiagnosticStatus = findings.Count > 0
            ? $"已分析 {versionId}：发现 {findings.Count} 个可能原因（点「打开日志目录」进一步排查）"
            : $"已分析 {versionId}：未发现常见崩溃原因（可点「导出诊断包」把日志发出来排查）";
    }

    [RelayCommand]
    private void AnalyzeCrash()
    {
        var id = AppServices.Launcher.LastVersionId;
        if (string.IsNullOrEmpty(id))
        {
            DiagnosticStatus = "还没有启动过游戏，无法分析。先启动一次游戏，退出后再来分析。";
            return;
        }
        Analyze(id);
    }

    [RelayCommand]
    private void OpenLogs()
    {
        var id = _lastAnalyzedVersion.Length > 0 ? _lastAnalyzedVersion : AppServices.Launcher.LastVersionId;
        if (string.IsNullOrEmpty(id)) return;
        var dir = Path.Combine(AppPaths.VersionDir(id), "logs");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var path = DiagnosticExporter.Export();
        DiagnosticStatus = path != null
            ? "诊断包已导出：" + path
            : "导出失败，详见上方日志";
        if (path != null) LogService.Info("诊断包已导出: " + path);
    }

    private void AddLine(string line)
    {
        Lines.Add(line);
        if (Matches(line)) Filtered.Add(line);
        if (Lines.Count > 1000) { Lines.RemoveAt(0); ApplyFilter(); }
    }

    private bool Matches(string l) =>
        Filter.Length == 0 || l.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var l in Lines) if (Matches(l)) Filtered.Add(l);
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        Filtered.Clear();
    }
}
