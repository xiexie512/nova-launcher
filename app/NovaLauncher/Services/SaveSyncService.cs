using System;
using System.IO;
using System.IO.Compression;

namespace NovaLauncher.Services;

/// <summary>
/// 跨设备存档管理：导出/导入世界存档（zip）。
/// 近场（星闪/蓝牙）传输属于硬件能力：当前版本提供「导出存档文件 + 手动/网盘互传」，
/// 近场 SDK 接入点为 Labs 页预留（cores/ 目录加载联机/传输核心）。
/// </summary>
public static class SaveSyncService
{
    /// <summary>导出指定版本的存档到 exports/。</summary>
    public static string? Export(string versionId)
    {
        var saves = Path.Combine(AppPaths.GameDir(versionId), "saves");
        if (!Directory.Exists(saves)) return null;
        Directory.CreateDirectory(AppPaths.Exports);
        var outFile = Path.Combine(AppPaths.Exports, $"saves-{versionId}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ZipFile.CreateFromDirectory(saves, outFile, CompressionLevel.Optimal, false);
        return outFile;
    }

    /// <summary>导入存档 zip 到指定版本的游戏目录。</summary>
    public static bool Import(string versionId, string zipFile)
    {
        if (!File.Exists(zipFile)) return false;
        var saves = Path.Combine(AppPaths.GameDir(versionId), "saves");
        Directory.CreateDirectory(saves);
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            foreach (var entry in zip.Entries)
            {
                var target = Path.Combine(saves, entry.FullName);
                if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
            return true;
        }
        catch { return false; }
    }
}
