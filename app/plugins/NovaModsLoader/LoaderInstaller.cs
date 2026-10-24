using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovaLauncher.PluginApi;

namespace NovaModsLoader;

/// <summary>
/// 4 大模组加载器安装。
/// Fabric / Quilt：走官方 Meta API 生成「合并版」版本 JSON（原版库 + 加载器库 + 加载器 mainClass），
///                   不跑安装器，最稳定，交给 Nova 启动器自动下载全部库。
/// Forge / NeoForge：下载官方 installer 无头运行（--installClient），把产出的版本 JSON 合并进 Nova 布局。
/// 全部多版本兼容（按 NovaModsLoader.mcVersion 指定 MC 版本）。
/// </summary>
public static class LoaderInstaller
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NovaModsLoader/1.0");
        c.Timeout = TimeSpan.FromSeconds(60);
        return c;
    }

    public static string VersionsDir(string gameRoot) => Path.Combine(gameRoot, "versions");
    public static string VersionDir(string gameRoot, string id) => Path.Combine(gameRoot, "versions", id);
    public static string VersionJsonPath(string gameRoot, string id) => Path.Combine(gameRoot, "versions", id, id + ".json");
    public static string LibrariesDir(string gameRoot) => Path.Combine(gameRoot, "libraries");

    // ---------------- Fabric ----------------
    public static string InstallFabric(ILauncherApi api, Action<string> status)
    {
        var mc = api.GetSetting("mcVersion", "1.21.1");
        var id = "fabric-loader-" + mc;
        status($"开始安装 Fabric（MC {mc}）…");

        var json = HttpGetJson($"https://meta.fabricmc.net/v2/versions/loader/{mc}");
        var arr = json as JsonArray ?? throw new Exception("Fabric Meta 返回异常");
        if (arr.Count == 0) throw new Exception($"MC {mc} 没有可用 Fabric 加载器");
        var entry = arr[0]!.AsObject();
        var loaderVer = entry["loader"]!["version"]!.GetValue<string>();
        var libs = FlattenLibraries(entry["launcherMeta"]?["libraries"]);
        var mainClass = entry["launcherMeta"]?["mainClass"]?["client"]?.GetValue<string>()
                        ?? "net.fabricmc.loader.impl.launch.knot.KnotClient";

        var vanilla = FetchVanillaJson(api, mc);
        MergeAndWrite(api, vanilla, id, libs, mainClass);
        status($"Fabric 完成：版本「{id}」已就绪，加载器 {loaderVer}。在版本页选择该版本启动即可。");
        return $"安装 Fabric {loaderVer} (MC {mc}) → 版本 {id}";
    }

    // ---------------- Quilt ----------------
    public static string InstallQuilt(ILauncherApi api, Action<string> status)
    {
        var mc = api.GetSetting("mcVersion", "1.21.1");
        var id = "quilt-loader-" + mc;
        status($"开始安装 Quilt（MC {mc}）…");

        var json = HttpGetJson($"https://meta.quiltmc.org/v3/versions/loader/{mc}");
        var arr = json as JsonArray ?? throw new Exception("Quilt Meta 返回异常");
        if (arr.Count == 0) throw new Exception($"MC {mc} 没有可用 Quilt 加载器");
        var entry = arr[0]!.AsObject();
        var loaderVer = entry["loader"]?["version"]?.GetValue<string>() ?? "?";
        var libs = FlattenLibraries(entry["launcherMeta"]?["libraries"]);
        var mainClass = entry["launcherMeta"]?["mainClass"]?["client"]?.GetValue<string>()
                        ?? "org.quiltmc.loader.impl.launch.knot.KnotClient";

        var vanilla = FetchVanillaJson(api, mc);
        MergeAndWrite(api, vanilla, id, libs, mainClass);
        status($"Quilt 完成：版本「{id}」已就绪，加载器 {loaderVer}。");
        return $"安装 Quilt {loaderVer} (MC {mc}) → 版本 {id}";
    }

    // ---------------- Forge ----------------
    public static string InstallForge(ILauncherApi api, Action<string> status)
    {
        var mc = api.GetSetting("mcVersion", "1.21.1");
        var id = "forge-" + mc;
        status($"准备 Forge（MC {mc}）：查询官方 maven…");

        var meta = HttpGetString("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
        var version = PickMavenVersion(meta, mc, "forge");
        if (version == null) throw new Exception($"MC {mc} 没有匹配的 Forge 版本");

        var installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}/forge-{version}-installer.jar";
        var stage = Path.Combine(Path.GetTempPath(), "nova-forge-" + version.Replace('.', '_').Replace('-', '_'));
        Directory.CreateDirectory(stage);
        var installer = Path.Combine(stage, "forge-installer.jar");
        status($"下载 Forge 安装器 {version}…");
        DownloadTo(installerUrl, installer, status);

        var java = FindJava();
        if (java == null) throw new Exception("未找到 Java，无法运行 Forge 安装器");
        status($"运行 Forge 安装器（{version}），这一步较慢…");
        var target = Path.Combine(stage, "client");
        Directory.CreateDirectory(target);
        Run(java, $"\"{installer}\" --installClient \"{target}\" --quick", status, 5 * 60);

        // 定位产出的版本目录
        var vdir = FindInstalledVersion(target, version);
        var builtJson = vdir != null
            ? Path.Combine(target, "versions", vdir, vdir + ".json")
            : Path.Combine(target, vdir ?? "", (vdir ?? "") + ".json");
        if (!File.Exists(builtJson)) throw new Exception("Forge 安装器未产出版本 JSON");

        var built = JsonNode.Parse(File.ReadAllText(builtJson))!.AsObject();
        ImportLibraries(built, target, api);
        var libs = built["libraries"]?.AsArray() ?? new JsonArray();
        var mainClass = built["mainClass"]?.GetValue<string>() ?? "net.minecraftforge.client.ClientLaunchWrapper";

        var vanilla = FetchVanillaJson(api, mc);
        MergeAndWrite(api, vanilla, id, libs, mainClass);
        status($"Forge 完成：版本「{id}」已就绪（{version}）。");
        return $"安装 Forge {version} (MC {mc}) → 版本 {id}";
    }

    // ---------------- NeoForge ----------------
    public static string InstallNeoForge(ILauncherApi api, Action<string> status)
    {
        var mc = api.GetSetting("mcVersion", "1.21.1");
        var id = "neoforge-" + mc;
        status($"准备 NeoForge（MC {mc}）：查询官方 maven…");

        var meta = HttpGetString("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
        var version = PickMavenVersion(meta, mc, "neoforge");
        if (version == null) throw new Exception($"MC {mc} 没有匹配的 NeoForge 版本");

        var installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";
        var stage = Path.Combine(Path.GetTempPath(), "nova-neoforge-" + version.Replace('.', '_').Replace('-', '_'));
        Directory.CreateDirectory(stage);
        var installer = Path.Combine(stage, "neoforge-installer.jar");
        status($"下载 NeoForge 安装器 {version}…");
        DownloadTo(installerUrl, installer, status);

        var java = FindJava();
        if (java == null) throw new Exception("未找到 Java，无法运行 NeoForge 安装器");
        status($"运行 NeoForge 安装器（{version}），这一步较慢…");
        var target = Path.Combine(stage, "client");
        Directory.CreateDirectory(target);
        Run(java, $"\"{installer}\" --installClient \"{target}\"", status, 5 * 60);

        var vdir = FindInstalledVersion(target, version);
        var builtJson = Path.Combine(target, "versions", vdir ?? "", (vdir ?? "") + ".json");
        if (!File.Exists(builtJson)) throw new Exception("NeoForge 安装器未产出版本 JSON");

        var built = JsonNode.Parse(File.ReadAllText(builtJson))!.AsObject();
        ImportLibraries(built, target, api);
        var libs = built["libraries"]?.AsArray() ?? new JsonArray();
        var mainClass = built["mainClass"]?.GetValue<string>()
                        ?? "cpw.mods.modlauncher.Launcher";

        var vanilla = FetchVanillaJson(api, mc);
        MergeAndWrite(api, vanilla, id, libs, mainClass);
        status($"NeoForge 完成：版本「{id}」已就绪（{version}）。");
        return $"安装 NeoForge {version} (MC {mc}) → 版本 {id}";
    }

    // ---------------- 公共步骤 ----------------

    /// <summary>取原版 MC 版本 JSON（优先本地已装，否则从官方清单拉取）。</summary>
    private static JsonObject FetchVanillaJson(ILauncherApi api, string mc)
    {
        var local = VersionJsonPath(api.GameRoot, mc);
        if (File.Exists(local))
            return (JsonNode.Parse(File.ReadAllText(local))?.AsObject()) ?? throw new Exception($"版本 JSON 解析失败: {mc}");

        var manifest = HttpGetJson("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json")!.AsObject();
        foreach (var v in manifest["versions"]!.AsArray())
        {
            if (v!["id"]!.GetValue<string>() == mc)
            {
                var url = v["url"]!.GetValue<string>();
                return HttpGetJson(url)!.AsObject();
            }
        }
        throw new Exception($"MC {mc} 不在官方版本清单中");
    }

    /// <summary>合并：原版 JSON + 加载器库 + 加载器 mainClass，写成 Nova 可启动的独立版本。</summary>
    private static void MergeAndWrite(ILauncherApi api, JsonObject vanilla, string id, JsonArray extraLibs, string mainClass)
    {
        // 去重：按 downloads.artifact.path（或 name）去重
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
        if (vanilla["jar"] != null) vanilla.Remove("jar");

        var dir = VersionDir(api.GameRoot, id);
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

    /// <summary>
    /// 把加载器 Meta 返回的 libraries（可能是 {client,common,server} 字典或数组，条目为
    /// {name,url,sha1,size}）转换为 Nova 可识别的标准库格式 {name, downloads:{artifact:{path,url,sha1,size}}}。
    /// 只保留客户端需要的 common + client 分组。
    /// </summary>
    private static JsonArray FlattenLibraries(JsonNode? libsNode)
    {
        var outArr = new JsonArray();
        if (libsNode == null) return outArr;

        IEnumerable<JsonNode?> source;
        if (libsNode is JsonArray arr)
        {
            source = arr;
        }
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
            var artifact = new JsonObject
            {
                ["path"] = path,
                ["sha1"] = lib!["sha1"]?.GetValue<string>() ?? "",
                ["size"] = lib["size"]?.GetValue<long>() ?? 0,
                ["url"] = (lib["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/") + "/" + path,
            };
            var entry = new JsonObject
            {
                ["name"] = name,
                ["downloads"] = new JsonObject { ["artifact"] = artifact },
            };
            outArr.Add(entry);
        }
        return outArr;
    }

    /// <summary>由 Maven 坐标 name 推导相对路径（group/artifact/version/file.jar）。</summary>
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

    /// <summary>把安装器产物中、已存在于 staging 的库复制到 Nova libraries 目录（其余留给 Nova 自动下载）。</summary>
    private static void ImportLibraries(JsonObject built, string stage, ILauncherApi api)
    {
        var libsDir = LibrariesDir(api.GameRoot);
        Directory.CreateDirectory(libsDir);
        var libs = built["libraries"]?.AsArray();
        if (libs == null) return;
        foreach (var lib in libs)
        {
            var path = lib?["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(path)) continue;
            var src = Path.Combine(stage, "libraries", path);
            if (!File.Exists(src)) src = Path.Combine(stage, "libs", path);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(libsDir, path);
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

    /// <summary>从 maven-metadata.xml 中挑出匹配该 MC 的版本号。</summary>
    private static string? PickMavenVersion(string xml, string mc, string kind)
    {
        // <version>1.21.1-52.0.33</version> 之类
        var matches = System.Text.RegularExpressions.Regex.Matches(xml, @"<version>([^<]+)</version>")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Where(v => v.StartsWith(mc + "-"))
            .OrderByDescending(v => v)
            .ToList();
        if (matches.Count == 0) return null;
        return matches[0];
    }

    private static string? FindJava()
    {
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(jh) && File.Exists(Path.Combine(jh, "bin", "java"))) return Path.Combine(jh, "bin", "java");
        try { return "java"; } catch { return null; }
    }

    private static void Run(string java, string args, Action<string> status, int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = java,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
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
            status("安装器退出码 " + p.ExitCode + "\n" + (errT.Result.Length > 500 ? errT.Result[..500] : errT.Result));
            throw new Exception("安装器运行失败，详见上方输出");
        }
    }

    private static void DownloadTo(string url, string dest, Action<string> status)
    {
        using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        using var fs = File.Create(dest);
        using var src = resp.Content.ReadAsStream();
        var buf = new byte[81920];
        long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            fs.Write(buf, 0, n);
            done += n;
            if (total > 0 && done % (4 * 1024 * 1024) < buf.Length)
                status($"下载中… {done / 1024 / 1024} MB / {total / 1024 / 1024} MB");
        }
    }

    private static JsonNode? HttpGetJson(string url)
    {
        var s = HttpGetString(url);
        return JsonNode.Parse(s);
    }

    private static string HttpGetString(string url)
    {
        using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}: {url}");
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
