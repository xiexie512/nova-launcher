using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class AccountsViewModel : ObservableObject
{
    private string _status = "";
    private bool _busy;

    public ObservableCollection<GameAccount> Accounts { get; } = new();

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public AccountsViewModel() => Refresh();

    public void Refresh()
    {
        Accounts.Clear();
        foreach (var a in SettingsService.S.Accounts) Accounts.Add(a);
    }

    [RelayCommand]
    private void AddOffline()
    {
        // 优先使用设置中的「离线默认用户名」，留空则自动生成 Steve+随机
        var name = SettingsService.S.OfflineUsername;
        if (string.IsNullOrWhiteSpace(name)) name = "Steve" + new Random().Next(100, 999);
        var acc = AuthService.AddOffline(name);
        Accounts.Add(acc);
        Status = "已添加离线账户（改名可删掉重建，离线 UUID 与名字绑定）";
    }

    [RelayCommand]
    private async Task AddYggdrasilAsync(string? serverAndUser)
    {
        // 参数格式: server|username|password（由 View 弹窗收集后拼接）
        if (string.IsNullOrEmpty(serverAndUser)) return;
        var parts = serverAndUser.Split('|');
        if (parts.Length < 3) { Status = "参数错误"; return; }
        Busy = true;
        Status = "正在验证外置登录…";
        try
        {
            var acc = await AuthService.LoginYggdrasilAsync(parts[0], parts[1], parts[2]);
            Accounts.Add(acc);
            Status = $"登录成功：{acc.Name}";
        }
        catch (Exception e)
        {
            Status = "外置登录失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    public string MicrosoftClientId => SettingsService.S.MicrosoftClientId;

    [RelayCommand]
    private async Task AddMicrosoftAsync()
    {
        var clientId = SettingsService.S.MicrosoftClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Status = "需要先配置微软应用 Client ID：设置 → 微软登录 Client ID（Azure 应用注册）";
            return;
        }
        Busy = true;
        try
        {
            var acc = await AuthService.LoginMicrosoftAsync(clientId,
                msg => { Status = msg; return Task.CompletedTask; },
                new Progress<string>(m => Status = m),
                CancellationToken.None);
            if (acc != null)
            {
                Accounts.Add(acc);
                Status = $"微软账户 {acc.Name} 登录成功";
            }
        }
        catch (Exception e)
        {
            Status = "微软登录失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Select(GameAccount? acc)
    {
        if (acc == null) return;
        AuthService.SetSelected(acc);
        Refresh();
        Status = $"已切换默认账户：{acc.Name}";
    }

    [RelayCommand]
    private void Remove(GameAccount? acc)
    {
        if (acc == null) return;
        AuthService.Remove(acc);
        Refresh();
        Status = "已删除账户";
    }
}
