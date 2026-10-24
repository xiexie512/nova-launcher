using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NovaLauncher.Services;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

public partial class LabsView : UserControl
{
    public LabsView()
    {
        InitializeComponent();
        Loaded += (_, _) => FillVersions();
    }

    private void FillVersions()
    {
        VersionCombo.Items.Clear();
        if (!System.IO.Directory.Exists(AppPaths.Versions)) return;
        foreach (var dir in System.IO.Directory.GetDirectories(AppPaths.Versions))
        {
            var id = System.IO.Path.GetFileName(dir);
            if (VersionService.IsInstalled(id)) VersionCombo.Items.Add(id);
        }
    }

    private async void ImportCore_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LabsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择联机核心文件（dll/jar）",
            AllowMultiple = false,
        });
        if (files.Count > 0) vm.ImportCoreCommand.Execute(files[0].TryGetLocalPath());
    }

    private void ExportSaves_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LabsViewModel vm) return;
        vm.ExportSavesCommand.Execute(VersionCombo.SelectedItem as string);
    }

    private async void ImportSaves_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LabsViewModel vm) return;
        var version = VersionCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(version)) { vm.Status = "请先在上方选择版本"; return; }
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择存档备份 zip",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType> { new("存档") { Patterns = new[] { "*.zip" } } },
        });
        if (files.Count > 0) vm.ImportSavesCommand.Execute(version + "|" + files[0].TryGetLocalPath());
    }
}
