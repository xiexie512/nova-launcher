using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NovaLauncher.Kernel;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

public sealed class ModpackInfo
{
    public string Name { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string McVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public long SizeMb { get; init; }
    public bool Installed { get; init; }
}

/// <summary>整合包管理：Modrinth .mrpack / CurseForge .zip 导入，实例导出。</summary>
public sealed class ModpackService
{
    private readonly VersionService _versions;

    public ModpackService(VersionService versions) => _versions = versions;

    public ObservableCollection<ModpackInfo> Instances { get; } = new();

    public void Refresh()
    {
        Instances.Clear();
        if (!Directory.Exists(AppPaths.Versions)) return;
        foreach (var dir in Directory.GetDirectories(AppPaths.Versions))
        {
            var meta = Path.Combine(dir, "pack.json");
            if (!File.Exists(meta)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                var r = doc.RootElement;
                Instances.Add(new ModpackInfo
                {
                    Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir),
                    VersionId = Path.GetFileName(dir),
                    McVersion = r.TryGetProperty("mcVersion", out var m) ? m.GetString() ?? "" : "",
                    Loader = r.TryGetProperty("loader", out var l) ? l.GetString() ?? "" : "",
                    SizeMb = DirSize(dir) / 1024 / 1024,
                    Installed = VersionService.IsInstalled(Path.GetFileName(dir)),
                });
            }
            catch { }
        }
    }

    private static long DirSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>导入整合包文件（.mrpack 或含 manifest.json 的 .zip）。</summary>
    public async Task<(bool ok, string message)> ImportAsync(string file, IProgress<string>? progress = null)
    {
        if (!File.Exists(file)) return (false, "文件不存在");
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.Entries.Any(e => e.FullName == "modrinth.index.json"))
                return await ImportMrpackAsync(zip, file, progress);
            if (zip.Entries.Any(e => e.FullName == "manifest.json"))
                return await ImportCurseforgeAsync(zip, file, progress);
            return (false, "无法识别的整合包格式（需要 Modrinth .mrpack 或 CurseForge .zip）");
        }
        catch (Exception e)
        {
            return (false, "导入失败: " + e.Message);
        }
    }

    private async Task<(bool, string)> ImportMrpackAsync(ZipArchive zip, string file, IProgress<string>? progress)
    {
        var entry = zip.GetEntry("modrinth.index.json")!;
        using var sr = new StreamReader(entry.Open());
        using var doc = JsonDocument.Parse(sr.ReadToEnd());
        var r = doc.RootElement;

        var mcVersion = r.TryGetProperty("dependencies", out var deps) && deps.TryGetProperty("minecraft", out var m)
            ? m.GetString() ?? "" : "";
        if (mcVersion.Length == 0) return (false, "整合包缺少 Minecraft 版本信息");

        // 识别加载器（fabric / quilt / forge / neoforge，按优先级取第一个存在的）
        string loaderKind = "", loaderVer = "";
        var depNames = new[] { "fabric-loader", "quilt-loader", "neoforge", "forge" };
        if (r.TryGetProperty("dependencies", out var deps2))
        {
            foreach (var depName in depNames)
            {
                if (deps2.TryGetProperty(depName, out var dv))
                {
                    loaderKind = depName switch
                    {
                        "fabric-loader" => "fabric",
                        "quilt-loader" => "quilt",
                        _ => depName,
                    };
                    loaderVer = dv.GetString() ?? "";
                    break;
                }
            }
        }

        var name = Sanitize(Path.GetFileNameWithoutExtension(file));
        var id = name;
        if (VersionService.IsInstalled(id)) id = name + "-" + DateTime.Now.ToString("HHmmss");
        Directory.CreateDirectory(AppPaths.VersionDir(id));
        Directory.CreateDirectory(AppPaths.GameDir(id));

        // 1. 加载器版本 JSON（作为可启动版本）；Forge/NeoForge 获取失败时降级（仍导入整合包主体）
        string? loaderJson = null;
        if (loaderKind.Length > 0)
        {
            progress?.Report($"获取 {loaderKind} 加载器元数据…");
            loaderJson = await FetchLoaderProfileJsonAsync(mcVersion, loaderKind, loaderVer);
            if (loaderJson != null)
            {
                File.WriteAllText(AppPaths.VersionJson(id), loaderJson);
            }
            else if (loaderKind is "fabric" or "quilt")
            {
                return (false, $"获取 {loaderKind} 加载器元数据失败（网络受限？请稍后重试）");
            }
            // forge / neoforge：拿不到元数据不阻断导入，主体继续，最后提示手动安装
        }

        // 2. 下载 Minecraft 本体（客户端/库/资源）
        progress?.Report("下载 Minecraft 本体…");
        var entry2 = new VersionEntry { Id = id, Url = "" };
        await _versions.InstallVersionAsync(entry2);

        // 3. 下载 mods（客户端 mod）
        var modJobs = new List<DownloadJob>();
        if (r.TryGetProperty("files", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                var env = f.TryGetProperty("env", out var e2) ? e2 : default;
                var isServerOnly = env.ValueKind == JsonValueKind.Object &&
                                   env.TryGetProperty("server", out var sv) && sv.GetString() == "required";
                var isClientUnsupported = env.ValueKind == JsonValueKind.Object &&
                                          env.TryGetProperty("client", out var cv) && cv.GetString() == "unsupported";
                if (isServerOnly || isClientUnsupported) continue;
                var path = f.TryGetProperty("path", out var p) ? p.GetString() : "";
                if (string.IsNullOrEmpty(path) || !path.StartsWith("mods/")) continue;
                var urls = f.TryGetProperty("downloads", out var d) ? d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                if (urls.Count == 0) continue;
                var sha1 = f.TryGetProperty("hashes", out var h) && h.TryGetProperty("sha1", out var s) ? s.GetString() : null;
                modJobs.Add(new DownloadJob
                {
                    Key = "mod:" + Path.GetFileName(path),
                    Dest = Path.Combine(AppPaths.GameDir(id), path),
                    Urls = urls,
                    Sha1 = sha1,
                });
            }
        }
        if (modJobs.Count > 0)
        {
            progress?.Report($"下载 {modJobs.Count} 个模组…");
            var (ok, fail) = await DownloadService.DownloadAsync(modJobs, SettingsService.S.Workers, null);
            if (fail > 0 && ok == 0) return (false, "模组下载失败（可能被屏蔽，请稍后重试或更换网络）");
        }

        // 4. overrides 覆盖
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("overrides/")))
        {
            var rel = e.FullName["overrides/".Length..];
            var target = Path.Combine(AppPaths.GameDir(id), rel);
            if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }

        // 5. 元数据
        var loaderLabel = loaderKind.Length > 0 ? $"{loaderKind} {loaderVer}" : "原版";
        File.WriteAllText(Path.Combine(AppPaths.VersionDir(id), "pack.json"),
            JsonSerializer.Serialize(new { name, mcVersion, loader = loaderLabel }));
        Refresh();
        var msg = $"整合包「{name}」导入完成（MC {mcVersion} / {loaderLabel}）";
        if (loaderJson == null && loaderKind is "forge" or "neoforge")
            msg += "。⚠ 加载器元数据获取失败，已导入整合包主体；请到「游戏版本」页用「下载版本」安装 " + loaderKind + " 后即可启动";
        return (true, msg);
    }

    /// <summary>获取模组加载器 profile JSON（可直接作为版本 JSON）：Fabric/Quilt 走官方 meta，Forge/NeoForge 走官方 maven（失败返回 null 由调用方降级）。</summary>
    private static async Task<string?> FetchLoaderProfileJsonAsync(string mc, string kind, string ver)
    {
        return await Task.Run(() =>
        {
            try
            {
                return kind switch
                {
                    "fabric" => NovaKernel.HttpGet($"https://meta.fabricmc.net/v2/versions/loader/{mc}/{ver}/profile/json", 60000),
                    "quilt" => NovaKernel.HttpGet($"https://meta.quiltmc.org/v3/versions/loader/{mc}/{ver}/profile/json", 60000),
                    "neoforge" => NovaKernel.HttpGet(
                        $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{ver}/neoforge-{ver}.json", 60000),
                    "forge" => NovaKernel.HttpGet(
                        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{ver}/forge-{mc}-{ver}.json", 60000),
                    _ => null,
                };
            }
            catch
            {
                return null;
            }
        });
    }

    private async Task<(bool, string)> ImportCurseforgeAsync(ZipArchive zip, string file, IProgress<string>? progress)
    {
        var entry = zip.GetEntry("manifest.json")!;
        using var sr = new StreamReader(entry.Open());
        using var doc = JsonDocument.Parse(sr.ReadToEnd());
        var r = doc.RootElement;

        var mcVersion = r.TryGetProperty("minecraft", out var mc) && mc.TryGetProperty("version", out var v)
            ? v.GetString() ?? "" : "";
        var loader = "";
        if (r.TryGetProperty("minecraft", out var mc2) && mc2.TryGetProperty("modLoaders", out var mls))
        {
            foreach (var ml in mls.EnumerateArray())
            {
                var id2 = ml.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                if (id2.Contains("forge")) loader = id2;
                else if (id2.Contains("fabric")) loader = id2;
            }
        }
        if (mcVersion.Length == 0) return (false, "整合包缺少 MC 版本信息");

        var name = Sanitize(Path.GetFileNameWithoutExtension(file));
        var id = name;
        if (VersionService.IsInstalled(id)) id = name + "-" + DateTime.Now.ToString("HHmmss");
        Directory.CreateDirectory(AppPaths.VersionDir(id));
        Directory.CreateDirectory(AppPaths.GameDir(id));

        // 基础版本 JSON
        var list = await _versions.FetchManifestAsync();
        var baseVer = list.FirstOrDefault(x => x.Id == mcVersion);
        if (baseVer == null) return (false, "清单中找不到 MC " + mcVersion);
        await VersionService.EnsureVersionJsonAsync(baseVer);
        File.Copy(AppPaths.VersionJson(baseVer.Id), AppPaths.VersionJson(id), true);
        await _versions.InstallVersionAsync(new VersionEntry { Id = id, Url = "" });

        // overrides
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("overrides/")))
        {
            var rel = e.FullName["overrides/".Length..];
            var target = Path.Combine(AppPaths.GameDir(id), rel);
            if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }

        // 模组下载需要 CurseForge API Key（设置中配置）；未配置则跳过并提示
        var key = SettingsService.S.CurseForgeKey;
        if (r.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                progress?.Report("未配置 CurseForge API Key，跳过模组下载（整合包本体与覆盖文件已就位）");
            }
            else
            {
                int got = 0, failed = 0;
                foreach (var f in files.EnumerateArray())
                {
                    var pid = f.TryGetProperty("projectID", out var p) ? p.GetInt32() : 0;
                    var fid = f.TryGetProperty("fileID", out var ff) ? ff.GetInt32() : 0;
                    if (pid == 0 || fid == 0) continue;
                    try
                    {
                        var meta = NovaKernel.HttpGetEx(
                            $"https://api.curseforge.com/v1/mods/{pid}/files/{fid}/download-url",
                            "x-api-key: " + key, 30000);
                        if (meta == null) { failed++; continue; }
                        using var md = JsonDocument.Parse(meta);
                        var url = md.RootElement.GetProperty("data").GetString();
                        if (url == null) { failed++; continue; }
                        var dest = Path.Combine(AppPaths.GameDir(id), "mods", $"{pid}-{fid}.jar");
                        var ok = await DownloadService.DownloadFileWithHeadersAsync(url, dest, null, "x-api-key: " + key);
                        if (ok) got++; else failed++;
                    }
                    catch { failed++; }
                }
                progress?.Report($"模组下载完成：成功 {got}，失败 {failed}");
            }
        }

        File.WriteAllText(Path.Combine(AppPaths.VersionDir(id), "pack.json"),
            JsonSerializer.Serialize(new { name, mcVersion, loader, curseforge = true }));
        Refresh();
        var note = loader.Contains("forge") ? "（Forge 加载器需在游戏目录中自行安装对应 Forge）" : "";
        return (true, $"CurseForge 整合包「{name}」导入完成{note}");
    }

    /// <summary>导出整合包：将实例（mods/配置/存档）打包为 zip。</summary>
    public static string? Export(string versionId, string outDir)
    {
        var dir = AppPaths.GameDir(versionId);
        if (!Directory.Exists(dir)) return null;
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, $"{versionId}-pack-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(outFile, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f);
            if (rel.StartsWith("natives") || rel.StartsWith("logs")) continue;
            zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
        }
        return outFile;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        return new string(chars);
    }
}
