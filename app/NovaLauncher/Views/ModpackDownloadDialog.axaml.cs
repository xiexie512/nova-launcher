using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NovaLauncher.Services;

namespace NovaLauncher.Views;

/// <summary>「整合包下载」对话框：Modrinth 搜索整合包，下载 .mrpack 并导入为版本。</summary>
public partial class ModpackDownloadDialog : Window
{
    private bool _busy;

    public ModpackDownloadDialog()
    {
        InitializeComponent();

        // MC 版本过滤：常用版本清单（从官方清单懒加载太慢，这里提供常见版本 + 自由输入由加载器版本过滤兜底）
        var common = new[]
        {
            "1.21.1", "1.21", "1.20.6", "1.20.4", "1.20.1", "1.19.4", "1.19.2",
            "1.18.2", "1.16.5", "1.12.2", "1.8.9", "1.7.10",
        };
        var items = new List<ComboBoxItem> { new() { Tag = "", Content = "全部 MC 版本" } };
        foreach (var m in common) items.Add(new ComboBoxItem { Tag = m, Content = "MC " + m });
        McFilterBox.ItemsSource = items;
        McFilterBox.SelectedIndex = 0;
    }

    private string McFilterSel => (McFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private string LoaderFilterSel => (LoaderFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Search_Click(object? sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0) { Status.Text = "请输入搜索关键词"; return; }
        if (_busy) return;
        _busy = true;
        SetBusy(true);
        Status.Text = $"正在搜索「{query}」…";
        try
        {
            var list = await ModrinthClient.SearchModpacksAsync(query, McFilterSel.Length > 0 ? McFilterSel : null, LoaderFilterSel.Length > 0 ? LoaderFilterSel : null);
            ResultsBox.ItemsSource = list;
            Status.Text = list.Count > 0 ? $"找到 {list.Count} 个整合包，请选择后点击「下载并导入」" : "没有找到匹配的整合包，试试更短的关键词";
        }
        catch (Exception ex)
        {
            Status.Text = "搜索失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (ResultsBox.SelectedItem is not ModrinthItem item)
        {
            Status.Text = "请先在列表中选择要下载的整合包";
            return;
        }
        var mc = McFilterSel.Length > 0 ? McFilterSel : null;
        var loader = LoaderFilterSel.Length > 0 ? LoaderFilterSel : null;

        _busy = true;
        SetBusy(true);
        var progress = new Progress<string>(s => Status.Text = s);
        string? tmpFile = null;
        try
        {
            Status.Text = $"获取 {item.Title} 的整合包文件…";
            var file = await ModrinthClient.GetLatestFileAsync(item.Slug, mc, loader, ".mrpack");
            tmpFile = Path.Combine(Path.GetTempPath(), "nova-pack-" + Guid.NewGuid().ToString("N")[..8] + ".mrpack");
            Status.Text = $"正在下载 {file.FileName}（{file.VersionNumber}）…";
            await ModrinthClient.DownloadAsync(file.Url, tmpFile, progress);

            Status.Text = "正在导入整合包（下载 MC 本体与模组）…";
            var (ok, msg) = await AppServices.Modpacks.ImportAsync(tmpFile, progress);
            Status.Text = ok ? "✓ " + msg : "导入失败: " + msg;
        }
        catch (Exception ex)
        {
            Status.Text = "下载失败: " + ex.Message;
            LogService.Error("整合包下载失败: " + ex);
        }
        finally
        {
            try { if (tmpFile != null && File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Progress.IsVisible = busy;
        DownloadBtn.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
    }
}
