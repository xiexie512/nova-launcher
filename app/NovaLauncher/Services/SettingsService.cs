using System;
using System.IO;
using System.Text.Json;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

public static class SettingsService
{
    private static LauncherSettings? _s;

    public static LauncherSettings S => _s ??= Load();

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch (Exception e)
        {
            LogService.Info("设置读取失败，使用默认值: " + e.Message);
        }
        return new LauncherSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Home);
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(_s ?? new LauncherSettings(), JsonOpts));
        }
        catch (Exception e)
        {
            LogService.Info("设置保存失败: " + e.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
