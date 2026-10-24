using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.Views;

/// <summary>「下载版本」对话框：分类选版本 + 4 大模组加载器 + 2 大光影加载器。</summary>
public partial class VersionDownloadDialog : Window
{
    private List<VersionEntry> _manifest = new();
    private bool _busy;

    public VersionDownloadDialog()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadManifestAsync();
    }

    private async Task LoadManifestAsync()
    {
        Status.Text = "正在获取版本清单…";
        try
        {
            _manifest = await AppServices.Versions.FetchManifestAsync();
            Status.Text = $"清单就绪，共 {_manifest.Count} 个版本";
            ApplyCategory();
        }
        catch (Exception e)
        {
            Status.Text = "获取版本清单失败: " + e.Message;
        }
    }

    private void CategoryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyCategory();

    private void ApplyCategory()
    {
        // XAML 初始化阶段（SelectedIndex="0" 会提前触发 SelectionChanged）VersionBox 尚未创建，直接跳过；
        // 清单就绪后 LoadManifestAsync 会再次调用 ApplyCategory 完成填充。
        if (VersionBox is null) return;
        if (CategoryBox.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "release";
        IEnumerable<VersionEntry> filtered = tag switch
        {
            "release" => _manifest.Where(v => v.Type == "release" && !LoaderService.IsAprilFoolsVersion(v.Id)),
            "snapshot" => _manifest.Where(v => v.Type == "snapshot" && !LoaderService.IsAprilFoolsVersion(v.Id)),
            "old" => _manifest.Where(v => v.Type is "old_alpha" or "old_beta"),
            "april" => _manifest.Where(v => LoaderService.IsAprilFoolsVersion(v.Id)),
            _ => _manifest,
        };
        var list = filtered.OrderByDescending(v => v.ReleaseTime).ToList();
        VersionBox.ItemsSource = list;
        VersionBox.SelectedIndex = list.Count > 0 ? 0 : -1;
        if (list.Count == 0 && Status is not null) Status.Text = "该分类没有可用版本";
        UpdateNote();
    }

    private void LoaderShaderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateNote();

    private string LoaderSel => (LoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private string ShaderSel => (ShaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void UpdateNote()
    {
        // 初始化阶段 LoaderBox/ShaderBox 的 SelectedIndex="0" 也会提前触发本方法，此时 NoteText 尚未创建。
        if (NoteText is null) return;
        var loader = LoaderSel;
        var shader = ShaderSel;
        if (shader == "iris" && loader is "forge" or "neoforge")
        {
            NoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F2A65A"));
            NoteText.Text = "⚠ Iris 光影需要 Fabric / Quilt 加载器，请先切换加载器，或选择「无」让启动器自动安装 Fabric。";
            return;
        }
        if (shader == "optifine" && loader is "fabric" or "quilt" or "neoforge")
        {
            NoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F2A65A"));
            NoteText.Text = "⚠ OptiFine 光影需要 Forge 加载器，请先切换加载器，或选择「无」让启动器自动安装 Forge。";
            return;
        }
        NoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7FE08C"));
        NoteText.Text = shader switch
        {
            "iris" => "✓ Iris 光影将随附 Sodium 一起装入版本 mods 目录。",
            "optifine" => "✓ OptiFine 将以 Forge 模组形式装入版本 mods 目录。",
            _ => loader.Length > 0
                ? $"✓ 将下载原版 + {loader} 加载器（生成独立版本）。"
                : "✓ 将下载纯净原版版本（不含任何加载器）。",
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (VersionBox.SelectedItem is not VersionEntry entry)
        {
            Status.Text = "请先选择要下载的版本";
            return;
        }
        var loader = LoaderSel;
        var shader = ShaderSel;
        if (shader == "iris" && loader.Length == 0) loader = "fabric";
        if (shader == "optifine" && loader.Length == 0) loader = "forge";
        if (shader == "iris" && loader is "forge" or "neoforge") { Status.Text = "Iris 与 Forge/NeoForge 不兼容，请更换加载器"; return; }
        if (shader == "optifine" && loader is "fabric" or "quilt" or "neoforge") { Status.Text = "OptiFine 与当前加载器不兼容，请改用 Forge"; return; }

        _busy = true;
        SetBusy(true);
        IProgress<string> progress = new Progress<string>(s => Status.Text = s);
        try
        {
            var mc = entry.Id;
            string versionId;
            if (loader.Length > 0)
            {
                progress.Report($"安装 {loader} 加载器（MC {mc}）…");
                versionId = await LoaderService.InstallLoaderAsync(mc, loader, entry, progress);
            }
            else
            {
                progress.Report($"正在下载原版 {mc}…");
                var ok = await AppServices.Versions.InstallVersionAsync(entry);
                if (!ok) throw new Exception("原版下载失败");
                versionId = mc;
            }
            if (shader == "iris") await LoaderService.InstallIrisAsync(mc, versionId, progress);
            if (shader == "optifine") await LoaderService.InstallOptiFineAsync(mc, versionId, progress);
            Status.Text = $"「{versionId}」下载完成 ✓，可在「游戏版本」页启动";
        }
        catch (Exception ex)
        {
            Status.Text = "下载失败: " + ex.Message;
            LogService.Error("版本下载失败: " + ex);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Progress.IsVisible = busy;
        DownloadBtn.IsEnabled = !busy;
        CategoryBox.IsEnabled = !busy;
        VersionBox.IsEnabled = !busy;
        LoaderBox.IsEnabled = !busy;
        ShaderBox.IsEnabled = !busy;
    }
}
