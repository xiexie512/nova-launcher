using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class ConsoleViewModel : ObservableObject
{
    private string _filter = "";
    private bool _autoScroll = true;

    public ObservableCollection<string> Lines { get; } = new();
    public ObservableCollection<string> Filtered { get; } = new();

    public string Filter
    {
        get => _filter;
        set { SetProperty(ref _filter, value); ApplyFilter(); }
    }
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }

    public ConsoleViewModel()
    {
        foreach (var l in LogService.Lines) AddLine(l);
        LogService.LineAppended += AddLine;
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
