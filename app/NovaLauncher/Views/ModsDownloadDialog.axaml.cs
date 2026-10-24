using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NovaLauncher.Services;

namespace NovaLauncher.Views;

/// <summary>「模组下载」对话框：Modrinth 搜索模组并下载到指定已装版本。</summary>
public partial class ModsDownloadDialog : Window
{
    private readonly ObservableCollection<InstalledVersionInfo> _targets;
    private bool _busy;

    public ModsDownloadDialog(ObservableCollection<InstalledVersionInfo> targets)
    {
        InitializeComponent();
        _targets = targets;

        TargetBox.ItemsSource = _targets;
        if (_targets.Count > 0) TargetBox.SelectedIndex = 0;

        // MC 版本过滤：全部 + 目标版本中的去重 MC 版本
        var mcs = _targets.Select(t => t.McVersion).Where(m => m.Length > 0).Distinct().OrderBy(m => m).ToList();
        McFilterBox.ItemsSource = BuildMcItems(mcs);
        McFilterBox.SelectedIndex = 0;
    }

    private static List<ComboBoxItem> BuildMcItems(List<string> mcs)
    {
        var items = new List<ComboBoxItem> { new() { Tag = "", Content = "全部 MC 版本" } };
        foreach (var m in mcs) items.Add(new ComboBoxItem { Tag = m, Content = "MC " + m });
        return items;
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
            var list = await ModrinthClient.SearchModsAsync(query, McFilterSel.Length > 0 ? McFilterSel : null, LoaderFilterSel.Length > 0 ? LoaderFilterSel : null);
            ResultsBox.ItemsSource = list;
            Status.Text = list.Count > 0 ? $"找到 {list.Count} 个模组，请选择后点击「下载到实例」" : "没有找到匹配的模组，试试更短的关键词";
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
        if (TargetBox.SelectedItem is not InstalledVersionInfo target)
        {
            Status.Text = "请先选择安装目标版本";
            return;
        }
        if (ResultsBox.SelectedItem is not ModrinthItem item)
        {
            Status.Text = "请先在列表中选择要下载的模组";
            return;
        }
        var mc = target.McVersion.Length > 0 ? target.McVersion : (McFilterSel.Length > 0 ? McFilterSel : null);
        var loader = target.Loader.Length > 0 ? target.Loader : (LoaderFilterSel.Length > 0 ? LoaderFilterSel : null);

        _busy = true;
        SetBusy(true);
        var progress = new Progress<string>(s => Status.Text = s);
        try
        {
            Status.Text = $"获取 {item.Title} 的可用版本…";
            var file = await ModrinthClient.GetLatestFileAsync(item.Slug, mc, loader, ".jar");
            var modsDir = Path.Combine(AppPaths.GameDir(target.Id), "mods");
            Directory.CreateDirectory(modsDir);
            var dest = Path.Combine(modsDir, file.FileName);
            Status.Text = $"正在下载 {file.FileName}（{file.VersionNumber}）…";
            await ModrinthClient.DownloadAsync(file.Url, dest, progress);
            Status.Text = $"✓ 已下载到 {target.Id} 的 mods 目录：{file.FileName}";
        }
        catch (Exception ex)
        {
            Status.Text = "下载失败: " + ex.Message;
            LogService.Error("模组下载失败: " + ex);
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
        SearchBox.IsEnabled = !busy;
    }
}
