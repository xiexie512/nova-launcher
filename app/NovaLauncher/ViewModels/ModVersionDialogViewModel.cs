using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>
/// 版本选择项包装器。
/// </summary>
public sealed partial class VersionSelectItem : ObservableObject
{
    public ModrinthVersion Version { get; }

    [ObservableProperty]
    private bool _isSelected;

    public VersionSelectItem(ModrinthVersion version) => Version = version;

    public string DisplayText => Version.DisplayText;
    public string FileName => Version.FileName;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                // 单选组：选中时通知父级
            }
        }
    }
}

/// <summary>
/// 模组版本选择对话框 ViewModel。
/// </summary>
public sealed partial class ModVersionDialogViewModel : ObservableObject
{
    private readonly ModSearchViewModel _parentVm;
    private readonly ModrinthItem _modItem;
    private string _status = "";

    public ObservableCollection<VersionSelectItem> Versions { get; } = new();

    public string ModTitle { get; }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool HasSelection => Versions.Any(v => v.IsSelected);

    public ModVersionDialogViewModel(ModSearchViewModel parentVm, ModrinthItem modItem)
    {
        _parentVm = parentVm;
        _modItem = modItem;
        ModTitle = modItem.Title;
        LoadVersionsAsync();
    }

    private async void LoadVersionsAsync()
    {
        Status = "加载版本列表...";
        try
        {
            var versions = await ModrinthClient.GetAllVersionsAsync(_modItem.ProjectId, _parentVm.SelectedMcVersion, _parentVm.SelectedLoader);
            foreach (var v in versions) Versions.Add(new VersionSelectItem(v));
            Status = $"共 {versions.Count} 个可用版本";
        }
        catch (Exception e)
        {
            Status = "加载失败: " + e.Message;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (View is Window w) w.Close();
    }

    [RelayCommand]
    private async Task Confirm()
    {
        var selected = Versions.FirstOrDefault(v => v.IsSelected);
        if (selected == null) return;

        if (View is Window w) w.Close();

        // 安装选中的版本
        await _parentVm.InstallSpecificVersionAsync(_modItem, selected.Version);
    }

    // 供 Dialog 关闭时访问
    public Window? View { get; set; }
}