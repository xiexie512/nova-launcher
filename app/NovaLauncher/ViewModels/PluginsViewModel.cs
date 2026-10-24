using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class PluginsViewModel : ObservableObject
{
    public ObservableCollection<PluginInfoModel> Plugins => PluginService.Plugins;
    public string PluginsDir => AppPaths.Plugins;

    [RelayCommand]
    private void Reload()
    {
        PluginService.LoadPlugins();
        OnPropertyChanged(nameof(Plugins));
    }

    [RelayCommand]
    private void OpenDir()
    {
        System.IO.Directory.CreateDirectory(AppPaths.Plugins);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.Plugins, UseShellExecute = true });
    }
}
