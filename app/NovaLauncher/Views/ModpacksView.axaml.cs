using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

public partial class ModpacksView : UserControl
{
    public ModpacksView() => InitializeComponent();

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModpacksViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择整合包文件",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("整合包") { Patterns = new[] { "*.mrpack", "*.zip" } },
                new("所有文件") { Patterns = new[] { "*" } },
            },
        });
        if (files.Count > 0)
            await vm.ImportCommand.ExecuteAsync(files[0].TryGetLocalPath());
    }

    private async void ModsStore_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModpacksViewModel vm) return;
        var top = TopLevel.GetTopLevel(this) as Window;
        if (top == null) return;
        var dlg = new ModsDownloadDialog(vm.InstalledTargets);
        await dlg.ShowDialog(top);
        vm.RefreshTargets();
    }

    private async void PackStore_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModpacksViewModel vm) return;
        var top = TopLevel.GetTopLevel(this) as Window;
        if (top == null) return;
        var dlg = new ModpackDownloadDialog();
        await dlg.ShowDialog(top);
        vm.RefreshTargets();
    }
}
