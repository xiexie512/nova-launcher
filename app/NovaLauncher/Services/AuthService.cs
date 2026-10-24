using System;
using System.Threading;
using System.Threading.Tasks;
using NovaLauncher.Kernel;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

/// <summary>三种登录方式：离线 / Yggdrasil 外置 / 微软正版（设备码流程）。</summary>
public static class AuthService
{
    public static GameAccount AddOffline(string name)
    {
        var acc = new GameAccount
        {
            Type = AccountType.Offline,
            Name = name.Trim(),
            Uuid = NovaKernel.OfflineUuid(name.Trim()),
            AccessToken = "0",
            ClientToken = Guid.NewGuid().ToString("N"),
            Selected = true,
        };
        foreach (var existing in SettingsService.S.Accounts) existing.Selected = false;
        SettingsService.S.Accounts.Add(acc);
        SettingsService.Save();
        return acc;
    }

    public static async Task<GameAccount> LoginYggdrasilAsync(string server, string username, string password)
    {
        var ct = Guid.NewGuid().ToString("N");
        var result = await Task.Run(() => NovaKernel.YggdrasilLogin(server.Trim(), username.Trim(), password, ct));
        var acc = new GameAccount
        {
            Type = AccountType.Yggdrasil,
            Name = result.GetProperty("name").GetString() ?? username.Trim(),
            Uuid = result.GetProperty("uuid").GetString() ?? "",
            AccessToken = result.GetProperty("accessToken").GetString() ?? "",
            ClientToken = result.TryGetProperty("clientToken", out var t) ? t.GetString() ?? ct : ct,
            AuthServer = server.Trim(),
            Selected = true,
        };
        foreach (var existing in SettingsService.S.Accounts) existing.Selected = false;
        SettingsService.S.Accounts.Add(acc);
        SettingsService.Save();
        return acc;
    }

    /// <summary>微软设备码登录。showCode 用于展示 user_code 和验证地址；cancelled 由调用方控制。</summary>
    public static async Task<GameAccount?> LoginMicrosoftAsync(
        string clientId,
        Func<string, Task> showCode,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var device = await Task.Run(() => NovaKernel.MicrosoftDeviceCode(clientId), ct);
        var deviceCode = device.GetProperty("device_code").GetString() ?? "";
        var userCode = device.GetProperty("user_code").GetString() ?? "";
        var verUri = device.TryGetProperty("verification_uri", out var v) ? v.GetString() : "https://microsoft.com/link";
        var interval = device.TryGetProperty("interval", out var i) ? i.GetInt32() : 5;
        var expiresIn = device.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900;
        await showCode($"请在浏览器打开 {verUri} 并输入代码：{userCode}");

        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        string? accessToken = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Math.Max(interval, 3) * 1000, ct);
            var poll = await Task.Run(() => NovaKernel.MicrosoftPollToken(clientId, deviceCode), ct);
            if (poll.TryGetProperty("access_token", out var at))
            {
                accessToken = at.GetString();
                break;
            }
            var err = poll.TryGetProperty("error", out var er) ? er.GetString() : "";
            if (err is "authorization_declined" or "expired_token" or "bad_verification_code")
                throw new Exception("微软登录失败: " + err);
            progress?.Report(err == "authorization_pending" ? "等待授权…" : "轮询中…");
        }
        if (accessToken == null) throw new Exception("微软授权超时");

        progress?.Report("正在获取 Xbox/Minecraft 凭证…");
        var finish = await Task.Run(() => NovaKernel.MicrosoftFinish(accessToken), ct);
        var acc = new GameAccount
        {
            Type = AccountType.Microsoft,
            Name = finish.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Uuid = finish.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "",
            AccessToken = finish.TryGetProperty("accessToken", out var a) ? a.GetString() ?? "" : "",
            ClientToken = Guid.NewGuid().ToString("N"),
            Selected = true,
        };
        if (acc.Name.Length == 0 || acc.Uuid.Length == 0) throw new Exception("未获取到正版档案（可能没有正版账号）");
        foreach (var existing in SettingsService.S.Accounts) existing.Selected = false;
        SettingsService.S.Accounts.Add(acc);
        SettingsService.Save();
        return acc;
    }

    public static void Remove(GameAccount acc)
    {
        SettingsService.S.Accounts.Remove(acc);
        SettingsService.Save();
    }

    public static void SetSelected(GameAccount acc)
    {
        foreach (var a in SettingsService.S.Accounts) a.Selected = a == acc;
        SettingsService.Save();
    }
}
