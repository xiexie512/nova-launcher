using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.PluginApi;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>插件页面上的一个操作按钮（绑定视图，自带点击命令，直接路由到持有页面的插件）。</summary>
public sealed partial class PluginActionItem : ObservableObject
{
    public string Id { get; }
    public string Label { get; }
    public string Icon { get; }
    public RelayCommand Click { get; }

    public PluginActionItem(PluginPageInfo page, PluginActionInfo a)
    {
        Id = a.Id;
        Label = a.Label;
        Icon = a.Icon;
        Click = new RelayCommand(() => PluginService.InvokeAction(page.Key, Id));
    }
}

/// <summary>插件注册的页面（侧栏动态项）。</summary>
public sealed partial class PluginPageViewModel : ObservableObject
{
    private readonly PluginPageInfo _page;
    private bool _isActive;

    public string Key { get; }
    public string Title { get; }
    public string Icon { get; }
    public ObservableCollection<PluginActionItem> Actions { get; } = new();

    public PluginPageViewModel(PluginPageInfo page)
    {
        _page = page;
        Key = page.Key;
        Title = page.Title;
        Icon = page.Icon;
        foreach (var a in page.Actions) Actions.Add(new PluginActionItem(page, a));
        page.BodyChanged += () => OnPropertyChanged(nameof(Body));
    }

    public string Body => _page.Body;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}
