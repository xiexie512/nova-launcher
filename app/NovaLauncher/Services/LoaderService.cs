using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NovaLauncher.Kernel;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

/// <summary>已安装版本信息（版本页 / 模组目标选择共用）。</summary>
public sealed class InstalledVersionInfo
{
    public string Id { get; init; } = "";
    public string McVersion { get; init; } = "";
    public bool IsPack { get; init; }
    public string Loader { get; init; } = "";      // fabric / forge / neoforge / quilt / ""
    public string Tag => IsPack ? "整合包" : Loader.Length > 0 ? Loader : "原版";
}

/// <summary>
/// 模组加载器 / 光影安装与已装版本扫描。
/// 4 大加载器：Fabric / Quilt（官方 Meta API 合并版本 JSON）、Forge / NeoForge（官方 installer 无头安装）。
/// 2 大光影：Iris（Modrinth 下载，随附 Sodium）、OptiFine（BMCLAPI 镜像 + 官网兜底）。
/// </summary>
public static class LoaderService
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NovaLauncher/1.0");
        c.Timeout = TimeSpan.FromSeconds(120);
        return c;
    }

    public const string BmclapiOptifineList = "https://bmclapi2.bangbang93.com/optifine/";

    // ================= 已安装版本扫描 =================

    /// <summary>扫描所有已安装版本（含整合包与加载器版本），附带 MC 版本与加载器信息。</summary>
    public static List<InstalledVersionInfo> ScanInstalled()
    {
        var list = new List<InstalledVersionInfo>();
        if (!Directory.Exists(AppPaths.Versions)) return list;
        foreach (var dir in Directory.GetDirectories(AppPaths.Versions))
        {
            var id = Path.GetFileName(dir);
            if (!VersionService.IsInstalled(id)) continue;
            var packJson = Path.Combine(dir, "pack.json");
            var isPack = File.Exists(packJson);
            var loader = DetectLoader(id);
            list.Add(new InstalledVersionInfo
            {
                Id = id,
                McVersion = ResolveMc(id),
                IsPack = isPack,
                Loader = loader,
            });
        }
        return list.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>由版本目录名推断加载器。</summary>
    public static string DetectLoader(string id)
    {
        if (id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase)) return "quilt";
        if (id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) return "forge";
        if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (id.StartsWith("nova-", StringComparison.OrdinalIgnoreCase)) return "fabric";
        return "";
    }

    /// <summary>解析一个已安装版本的 MC 版本号：pack.json → 加载器版本后缀 → 版本 JSON id。</summary>
    public static string ResolveMc(string id)
    {
        var packJson = Path.Combine(AppPaths.VersionDir(id), "pack.json");
        if (File.Exists(packJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packJson));
                var r = doc.RootElement;
                if (r.TryGetProperty("mcVersion", out var m) && m.GetString() is { Length: > 0 } mv) return mv;
            }
            catch { }
        }
        foreach (var (prefix, _) in LoaderPrefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = id[prefix.Length..];
                if (rest.Length > 0) return rest;
            }
        }
        try
        {
            var json = AppPaths.VersionJson(id);
            if (File.Exists(json))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("id", out var idp) && idp.GetString() is { Length: > 0 } vid)
                    return vid;
            }
        }
        catch { }
        return "";
    }

    private static readonly (string Prefix, string Loader)[] LoaderPrefixes =
    {
        ("fabric-loader-", "fabric"), ("quilt-loader-", "quilt"), ("neoforge-", "neoforge"), ("forge-", "forge"), ("nova-", "fabric"),
    };

    /// <summary>愚人节版本识别（官方清单里没有独立类型标记，按已知 id 精确匹配，避免误吞普通快照如 23w13a / 24w14a）。</summary>
    public static bool IsAprilFoolsVersion(string id)
    {
        if (KnownAprilFools.Contains(id)) return true;
        return id.StartsWith("3D Shareware", StringComparison.OrdinalIgnoreCase)
               || id.Contains("infinite", StringComparison.OrdinalIgnoreCase)
               || id.Contains("potato", StringComparison.OrdinalIgnoreCase)
               || id.Contains("craftmine", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> KnownAprilFools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Minecraft 2.0", "1.RV-Pre1", "3D Shareware v1.34", "15w14a", "20w14infinite",
        "23w13a_or_b", "24w14potato", "25w14craftmine",
    };

    // ================= 加载器安装 =================

    /// <summary>安装模组加载器（fabric / quilt / forge / neoforge），返回生成的版本 id。</summary>
    public static async Task<string> InstallLoaderAsync(string mc, string loader, VersionEntry? vanillaEntry, IProgress<string>? progress)
    {
        var id = loader switch
        {
            "fabric" => "fabric-loader-" + mc,
            "quilt" => "quilt-loader-" + mc,
            "forge" => "forge-" + mc,
            "neoforge" => "neoforge-" + mc,
            _ => throw new Exception("未知加载器: " + loader),
        };

        if (File.Exists(AppPaths.VersionJson(id)) && VersionService.IsInstalled(id))
        {
            progress?.Report($"版本「{id}」已存在，跳过加载器安装");
            return id;
        }

        switch (loader)
        {
            case "fabric": await InstallMetaLoaderAsync(mc, id, "fabric", vanillaEntry, progress); break;
            case "quilt": await InstallMetaLoaderAsync(mc, id, "quilt", vanillaEntry, progress); break;
            case "forge": await InstallInstallerLoaderAsync(mc, id, "forge", vanillaEntry, progress); break;
            case "neoforge": await InstallInstallerLoaderAsync(mc, id, "neoforge", vanillaEntry, progress); break;
        }

        progress?.Report("下载客户端与全部依赖库…");
        await AppServices.Versions.InstallVersionAsync(new VersionEntry { Id = id, Url = "" });
        return id;
    }

    /// <summary>Fabric / Quilt：官方 Meta API 生成合并版版本 JSON。</summary>
    private static async Task InstallMetaLoaderAsync(string mc, string id, string kind, VersionEntry? vanillaEntry, IProgress<string>? progress)
    {
        var metaUrl = kind == "fabric"
            ? $"https://meta.fabricmc.net/v2/versions/loader/{mc}"
            : $"https://meta.quiltmc.org/v3/versions/loader/{mc}";
        progress?.Report($"获取 {kind} 元数据（MC {mc}）…");
        var root = HttpGetJson(metaUrl) ?? throw new Exception($"{kind} Meta 返回异常");
        var arr = root as JsonArray ?? throw new Exception($"{kind} Meta 返回异常");
        if (arr.Count == 0) throw new Exception($"MC {mc} 没有可用 {kind} 加载器");
        var entry = arr[0]!.AsObject();
        var loaderVer = kind == "fabric"
            ? entry["loader"]?["version"]?.GetValue<string>() ?? "?"
            : entry["loader"]?["version"]?.GetValue<string>() ?? "?";
        var libs = FlattenLibraries(entry["launcherMeta"]?["libraries"]);
        var mainClass = kind == "fabric"
            ? entry["launcherMeta"]?["mainClass"]?["client"]?.GetValue<string>() ?? "net.fabricmc.loader.impl.launch.knot.KnotClient"
            : entry["launcherMeta"]?["mainClass"]?["client"]?.GetValue<string>() ?? "org.quiltmc.loader.impl.launch.knot.KnotClient";

        progress?.Report($"生成版本 JSON：{kind} {loaderVer}…");
        var vanilla = await FetchVanillaJsonAsync(mc, vanillaEntry);
        MergeAndWrite(vanilla, id, libs, mainClass);
        progress?.Report($"{kind} 版本「{id}」已就绪");
    }

    /// <summary>Forge / NeoForge：官方 installer 无头安装后合并版本 JSON。</summary>
    private static async Task InstallInstallerLoaderAsync(string mc, string id, string kind, VersionEntry? vanillaEntry, IProgress<string>? progress)
    {
        var (metaUrl, installerBase, mainFallback) = kind switch
        {
            "forge" => (
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                "https://maven.minecraftforge.net/net/minecraftforge/forge/",
                "net.minecraftforge.client.ClientLaunchWrapper"),
            _ => (
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/",
                "cpw.mods.modlauncher.Launcher"),
        };

        progress?.Report($"查询 {kind} 官方 Maven…");
        var xml = HttpGetString(metaUrl);
        var version = PickMavenVersion(xml, mc);
        if (version == null) throw new Exception($"MC {mc} 没有匹配的 {kind} 版本");

        var stage = Path.Combine(Path.GetTempPath(), "nova-" + kind + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        var installer = Path.Combine(stage, "installer.jar");
        var installerUrl = $"{installerBase}{version}/{kind}-{version}-installer.jar";
        progress?.Report($"下载 {kind} 安装器 {version}…");
        var rc = NovaKernel.DownloadFileEx(installerUrl, installer, null, "", 300000);
        if (rc != 0 || !File.Exists(installer)) throw new Exception($"{kind} 安装器下载失败");

        var java = FindInstallerJava();
        if (java == null) throw new Exception("未找到 Java 运行时（Forge/NeoForge 需要 Java 17+，可在设置中配置 Java 路径）");
        progress?.Report($"运行 {kind} 安装器（{version}），这一步较慢…");
        var target = Path.Combine(stage, "client");
        Directory.CreateDirectory(target);
        var args = new List<string> { installer, "--installClient", target };
        if (kind == "forge") args.Add("--quick");
        RunProcess(java, args, progress, 5 * 60);

        var vdir = FindInstalledVersion(target, version);
        var builtJson = vdir == null ? null : Path.Combine(target, "versions", vdir, vdir + ".json");
        if (builtJson == null || !File.Exists(builtJson)) throw new Exception($"{kind} 安装器未产出版本 JSON");

        var built = JsonNode.Parse(File.ReadAllText(builtJson))!.AsObject();
        ImportLibraries(built, target);
        var libs = built["libraries"]?.AsArray() ?? new JsonArray();
        var mainClass = built["mainClass"]?.GetValue<string>() ?? mainFallback;

        progress?.Report($"生成版本 JSON：{kind} {version}…");
        var vanilla = await FetchVanillaJsonAsync(mc, vanillaEntry);
        MergeAndWrite(vanilla, id, libs, mainClass);
        progress?.Report($"{kind} 版本「{id}」已就绪");
    }

    /// <summary>原版版本 JSON：优先本地已装，否则用清单 URL 拉取。</summary>
    private static async Task<JsonObject> FetchVanillaJsonAsync(string mc, VersionEntry? entry)
    {
        var local = AppPaths.VersionJson(mc);
        if (File.Exists(local))
            return JsonNode.Parse(File.ReadAllText(local))?.AsObject() ?? throw new Exception($"版本 JSON 解析失败: {mc}");

        string? body = null;
        if (!string.IsNullOrEmpty(entry?.Url))
        {
            try { body = NovaKernel.HttpGet(entry.Url, 60000); } catch { body = null; }
        }
        if (body == null)
        {
            var manifest = await AppServices.Versions.FetchManifestAsync();
            var hit = manifest.FirstOrDefault(v => v.Id == mc);
            if (hit != null && !string.IsNullOrEmpty(hit.Url))
            {
                try { body = NovaKernel.HttpGet(hit.Url, 60000); } catch { body = null; }
            }
        }
        if (body == null) throw new Exception("无法获取原版版本 JSON: " + mc);
        return JsonNode.Parse(body)?.AsObject() ?? throw new Exception("版本 JSON 解析失败: " + mc);
    }

    /// <summary>合并：原版 JSON + 加载器库 + 加载器 mainClass，写成独立可启动版本。</summary>
    private static void MergeAndWrite(JsonObject vanilla, string id, JsonArray extraLibs, string mainClass)
    {
        var seen = new HashSet<string>();
        var mergedLibs = new JsonArray();
        foreach (var lib in vanilla["libraries"]?.AsArray() ?? new JsonArray())
        {
            var key = LibKey(lib);
            if (seen.Add(key)) mergedLibs.Add(lib!.DeepClone());
        }
        foreach (var lib in extraLibs)
        {
            var key = LibKey(lib);
            if (seen.Add(key)) mergedLibs.Add(lib!.DeepClone());
        }

        vanilla["id"] = id;
        vanilla["libraries"] = mergedLibs;
        vanilla["mainClass"] = mainClass;
        vanilla.Remove("inheritsFrom");
        vanilla.Remove("jar");

        var dir = AppPaths.VersionDir(id);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "instance", "mods"));
        File.WriteAllText(Path.Combine(dir, id + ".json"), vanilla.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string LibKey(JsonNode? lib)
    {
        if (lib == null) return "";
        var path = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(path)) return path;
        return lib["name"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
    }

    /// <summary>把 Meta 返回的 libraries（common/client 分组）转为标准库格式。</summary>
    private static JsonArray FlattenLibraries(JsonNode? libsNode)
    {
        var outArr = new JsonArray();
        if (libsNode == null) return outArr;
        IEnumerable<JsonNode?> source;
        if (libsNode is JsonArray arr) source = arr;
        else if (libsNode is JsonObject obj)
        {
            var keep = new List<JsonNode?>();
            if (obj["common"] is JsonArray c) keep.AddRange(c.Cast<JsonNode?>());
            if (obj["client"] is JsonArray cl) keep.AddRange(cl.Cast<JsonNode?>());
            source = keep;
        }
        else return outArr;

        foreach (var lib in source)
        {
            var name = lib?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            var path = MavenPath(name);
            if (path.Length == 0) continue;
            var artifact = new JsonObject
            {
                ["path"] = path,
                ["sha1"] = lib!["sha1"]?.GetValue<string>() ?? "",
                ["size"] = lib["size"]?.GetValue<long>() ?? 0,
                ["url"] = (lib["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/") + "/" + path,
            };
            outArr.Add(new JsonObject
            {
                ["name"] = name,
                ["downloads"] = new JsonObject { ["artifact"] = artifact },
            });
        }
        return outArr;
    }

    private static string MavenPath(string name)
    {
        var at = name.IndexOf('@');
        var classifier = "";
        var v = name;
        if (at >= 0) { v = name[..at]; classifier = name[(at + 1)..]; }
        var p = v.Split(':');
        if (p.Length < 3) return "";
        var group = p[0].Replace('.', '/');
        var art = p[1];
        var ver = p[2];
        var file = art + "-" + ver + (classifier.Length > 0 ? "-" + classifier : "") + ".jar";
        return $"{group}/{art}/{ver}/{file}";
    }

    /// <summary>把安装器产物中、已存在于 staging 的库复制到 Nova libraries 目录。</summary>
    private static void ImportLibraries(JsonObject built, string stage)
    {
        Directory.CreateDirectory(AppPaths.Libraries);
        var libs = built["libraries"]?.AsArray();
        if (libs == null) return;
        foreach (var lib in libs)
        {
            var path = lib?["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(path)) continue;
            var src = Path.Combine(stage, "libraries", path);
            if (!File.Exists(src)) src = Path.Combine(stage, "libs", path);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(AppPaths.Libraries, path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, true);
            }
            catch { }
        }
    }

    private static string? FindInstalledVersion(string target, string expectedPrefix)
    {
        var versions = Path.Combine(target, "versions");
        if (!Directory.Exists(versions)) return null;
        foreach (var d in Directory.GetDirectories(versions))
            if (Path.GetFileName(d).StartsWith(expectedPrefix) || Path.GetFileName(d).Contains("forge"))
                return Path.GetFileName(d);
        return Directory.GetDirectories(versions).Select(Path.GetFileName).FirstOrDefault();
    }

    private static string? PickMavenVersion(string xml, string mc)
    {
        return Regex.Matches(xml, @"<version>([^<]+)</version>")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(v => v.StartsWith(mc + "-"))
            .OrderByDescending(v => v)
            .FirstOrDefault();
    }

    /// <summary>优先选 Java 17-21（Forge/NeoForge 安装器兼容区间），否则回退通用检测。</summary>
    public static string? FindInstallerJava()
    {
        foreach (var c in JavaService.CandidatePaths().Where(File.Exists))
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = c, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add("-version");
                using var p = Process.Start(psi);
                if (p == null) continue;
                var outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                var m = Regex.Match(outText, @"version\s+""?(\d+)");
                if (m.Success)
                {
                    var major = int.Parse(m.Groups[1].Value);
                    if (major >= 17 && major <= 21) return c;
                }
            }
            catch { }
        }
        return JavaService.FindJava();
    }

    private static void RunProcess(string java, List<string> args, IProgress<string>? progress, int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = java,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Exception("无法启动安装器进程");
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(true); } catch { }
            throw new Exception("安装器运行超时（" + timeoutSec + "s）");
        }
        if (p.ExitCode != 0)
        {
            var tail = (outT.Result + "\n" + errT.Result).Trim();
            if (tail.Length > 800) tail = tail[^800..];
            progress?.Report("安装器退出码 " + p.ExitCode + "\n" + tail);
            throw new Exception("安装器运行失败，详见上方输出");
        }
    }

    // ================= 光影安装 =================

    /// <summary>Iris 光影（需 Fabric/Quilt 加载器），随附 Sodium 渲染核心。</summary>
    public static async Task InstallIrisAsync(string mc, string versionId, IProgress<string>? progress)
    {
        var loader = DetectLoader(versionId) == "quilt" ? "quilt" : "fabric";
        var modsDir = Path.Combine(AppPaths.GameDir(versionId), "mods");
        Directory.CreateDirectory(modsDir);

        progress?.Report("获取 Iris 版本…");
        var iris = await ModrinthClient.GetLatestFileAsync("iris", mc, loader, ".jar");
        progress?.Report($"下载 Iris {iris.VersionNumber}…");
        await ModrinthClient.DownloadAsync(iris.Url, Path.Combine(modsDir, "iris-" + iris.FileName), progress);
        progress?.Report("Iris 已下载 ✓");

        progress?.Report("获取 Sodium 版本…");
        var sodium = await ModrinthClient.GetLatestFileAsync("sodium", mc, loader, ".jar");
        progress?.Report($"下载 Sodium {sodium.VersionNumber}…");
        await ModrinthClient.DownloadAsync(sodium.Url, Path.Combine(modsDir, "sodium-" + sodium.FileName), progress);
        progress?.Report("Sodium 已下载 ✓（光影就绪）");
    }

    /// <summary>OptiFine 光影（需 Forge 加载器）：BMCLAPI 镜像优先，官网链路兜底。</summary>
    public static async Task InstallOptiFineAsync(string mc, string versionId, IProgress<string>? progress)
    {
        var modsDir = Path.Combine(AppPaths.GameDir(versionId), "mods");
        Directory.CreateDirectory(modsDir);

        progress?.Report("查询 OptiFine 版本列表（BMCLAPI）…");
        var url = BmclapiOptifineList + mc;
        string? body = null;
        try { body = NovaKernel.HttpGet(url, 30000); } catch { body = null; }

        if (body != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // 优先正式版（非 preview 开头），其次最近的预览版
                    JsonElement? pick = null;
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var fn = e.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";
                        if (fn.Length == 0 || fn.StartsWith("preview", StringComparison.OrdinalIgnoreCase)) continue;
                        pick = e; break;
                    }
                    pick ??= doc.RootElement.EnumerateArray().FirstOrDefault(e =>
                        e.TryGetProperty("filename", out var f) && !string.IsNullOrEmpty(f.GetString()));
                    if (pick is { } p && p.TryGetProperty("_id", out var idp) && idp.GetString() is { Length: > 0 } oid)
                    {
                        var fn = p.TryGetProperty("filename", out var f2) ? f2.GetString() ?? "OptiFine.jar" : "OptiFine.jar";
                        progress?.Report($"下载 OptiFine {fn}…");
                        await DownloadFileWithRetryAsync($"https://bmclapi2.bangbang93.com/optifine/download/{oid}",
                            Path.Combine(modsDir, fn), progress);
                        progress?.Report("OptiFine 已下载 ✓（随附于 Forge 加载器 mods 目录）");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                progress?.Report("BMCLAPI OptiFine 解析失败，改用官网: " + e.Message);
            }
        }

        // 官网兜底：downloads → adloadx → downloadx
        progress?.Report("改用 optifine.net 官网…");
        var jar = await FetchOptiFineFromOfficialAsync(mc, progress);
        await DownloadFileWithRetryAsync(jar.Url, Path.Combine(modsDir, jar.FileName), progress);
        progress?.Report("OptiFine 已下载 ✓（官网）");
    }

    /// <summary>官网链路：解析下载页与中转页，拿到真实文件地址。</summary>
    private static async Task<(string Url, string FileName)> FetchOptiFineFromOfficialAsync(string mc, IProgress<string>? progress)
    {
        progress?.Report("获取官网下载页…");
        var page = await Http.GetStringAsync("https://optifine.net/downloads");
        var candidates = Regex.Matches(page, @"OptiFine_" + Regex.Escape(mc) + @"_[^""<'\s]+\.jar")
            .Cast<Match>().Select(m => m.Value).Distinct().ToList();
        if (candidates.Count == 0) throw new Exception("官网没有匹配 MC " + mc + " 的 OptiFine 版本");
        var file = candidates[0]; // 页面按版本倒序，第一个即最新

        progress?.Report("解析下载中转页…");
        using (var r = await Http.GetAsync("https://optifine.net/adloadx?f=" + Uri.EscapeDataString(file)))
        {
            r.EnsureSuccessStatusCode();
            var adPage = await r.Content.ReadAsStringAsync();
            var m = Regex.Match(adPage, @"(?:https?:)?//optifine\.net/downloadx\?[^'""]+");
            if (!m.Success) throw new Exception("官网中转页解析失败");
            var dlUrl = m.Value.StartsWith("//") ? "https:" + m.Value : m.Value;
            return (dlUrl, file);
        }
    }

    /// <summary>带 2 次重试的下载（镜像 CDN 偶发抽风）。</summary>
    private static async Task DownloadFileWithRetryAsync(string url, string dest, IProgress<string>? progress)
    {
        Exception? last = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await ModrinthClient.DownloadAsync(url, dest, progress);
                if (new FileInfo(dest).Length > 0) return;
                throw new Exception("下载内容为空");
            }
            catch (Exception e)
            {
                last = e;
                progress?.Report($"下载失败，重试 {i + 1}/3: {e.Message}");
                await Task.Delay(1000);
            }
        }
        throw last ?? new Exception("下载失败");
    }

    // ================= 通用 =================

    private static JsonNode? HttpGetJson(string url)
    {
        var s = HttpGetString(url);
        return JsonNode.Parse(s);
    }

    private static string HttpGetString(string url)
    {
        try
        {
            return NovaKernel.HttpGet(url, 30000) ?? throw new Exception("无响应");
        }
        catch (Exception e)
        {
            throw new Exception($"请求失败: {e.Message}");
        }
    }
}
