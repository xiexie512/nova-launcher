using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>功能实验室：陶瓦联机 / 星闪同步 / 投影预览 / AI Nova / 自制插件。</summary>
public sealed partial class LabsViewModel : ObservableObject
{
    private string _status = "";
    private bool _novaBusy;
    private string _novaInput = "";
    private string _novaOutput = "我是 Nova，启动器内置 AI 助手。\n可以在设置中配置 OpenAI 兼容接口（DeepSeek/通义/Ollama 等）后与我对话：\n- 游戏问题解答 / 装机指导\n- 模组冲突排查建议\n- 存档备份提醒\n- 整合包推荐";
    private readonly ObservableCollection<(string role, string content)> _chat = new();

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool NovaBusy { get => _novaBusy; set => SetProperty(ref _novaBusy, value); }
    public string NovaInput { get => _novaInput; set => SetProperty(ref _novaInput, value); }
    public string NovaOutput { get => _novaOutput; set => SetProperty(ref _novaOutput, value); }

    public string CoresDir => AppPaths.Cores;
    public string ExportDir => AppPaths.Exports;

    // ---------- 陶瓦联机 / 虚拟局域网 ----------
    [RelayCommand]
    private void ImportCore(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Status = "请选择联机核心文件（dll/jar）"; return; }
        Directory.CreateDirectory(AppPaths.Cores);
        var dest = Path.Combine(AppPaths.Cores, Path.GetFileName(path));
        File.Copy(path, dest, true);
        Status = $"已导入联机核心：{Path.GetFileName(path)}。联机 SDK 接入点已注册，可在插件中调用。";
    }

    [RelayCommand]
    private void OpenCoresDir()
    {
        Directory.CreateDirectory(AppPaths.Cores);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.Cores, UseShellExecute = true });
    }

    // ---------- 星闪/蓝牙 存档同步 ----------
    [RelayCommand]
    private void ExportSaves(string? versionId)
    {
        if (string.IsNullOrEmpty(versionId)) { Status = "请选择要导出的版本"; return; }
        var f = SaveSyncService.Export(versionId);
        Status = f != null ? $"存档已导出：{f}（可通过网盘/AirDrop 等传输到其他设备后导入）" : "该版本没有存档目录";
    }

    [RelayCommand]
    private void ImportSaves(string? versionAndZip)
    {
        if (string.IsNullOrEmpty(versionAndZip)) return;
        var parts = versionAndZip.Split('|');
        if (parts.Length < 2) { Status = "参数错误"; return; }
        var ok = SaveSyncService.Import(parts[0], parts[1]);
        Status = ok ? "存档导入完成 ✓" : "导入失败";
    }

    [RelayCommand]
    private void OpenExportDir()
    {
        Directory.CreateDirectory(AppPaths.Exports);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.Exports, UseShellExecute = true });
    }

    // ---------- 投影预览 ----------
    [RelayCommand]
    private void OpenProjectionDoc()
    {
        Status = "投影预览建议：安装 Litematica 模组后，将 .litematic 文件放入 versions/<版本>/instance/schematics/。空间模式全息预览需等 MC 内插件支持，接口已预留。";
    }

    // ---------- AI Nova ----------
    [RelayCommand]
    private async Task SendNovaAsync()
    {
        var text = NovaInput.Trim();
        if (text.Length == 0 || NovaBusy) return;
        NovaInput = "";
        NovaOutput = NovaOutput + "\n\n你：" + text + "\n\nNova：思考中…";
        NovaBusy = true;
        try
        {
            _chat.Add(("user", text));
            var reply = await NovaAiService.ChatAsync(_chat.ToList(), CancellationToken.None);
            _chat.Add(("assistant", reply));
            NovaOutput = NovaOutput[..NovaOutput.LastIndexOf("Nova：思考中…", StringComparison.Ordinal)] + "Nova：" + reply;
        }
        catch (Exception e)
        {
            NovaOutput = NovaOutput[..NovaOutput.LastIndexOf("Nova：思考中…", StringComparison.Ordinal)] + "Nova：" + e.Message;
        }
        finally { NovaBusy = false; }
    }

    [RelayCommand]
    private void OpenAiSettings() => Status = "请到 设置 → AI 助手 填写：接口地址、API Key、模型名（Nova 支持 DeepSeek/通义/Ollama 等 OpenAI 兼容接口）";
}
