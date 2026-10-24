using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NovaLauncher.Kernel;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

/// <summary>版本清单 / 版本解析 / 游戏文件安装。</summary>
public sealed class VersionService
{
    public const string MojangManifest = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    public const string BmclapiManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    public const string BmclapiBase = "https://bmclapi2.bangbang93.com";
    public const string MojangLibraries = "https://libraries.minecraft.net";
    public const string MojangResources = "https://resources.download.minecraft.net";

    public event Action<string, long, long, bool>? Progress;   // phaseKey, done, total, finished
    public event Action<string>? Phase;

    public async Task<List<VersionEntry>> FetchManifestAsync()
    {
        return await Task.Run(() =>
        {
            var urls = new List<string>();
            if (SettingsService.S.UseMojang) urls.Add(MojangManifest);
            if (SettingsService.S.UseBmclapi) urls.Add(BmclapiManifest);
            urls.Add(MojangManifest); // 兜底

            string? body = null;
            string? lastErr = null;
            foreach (var u in urls.Distinct())
            {
                try { body = NovaKernel.HttpGet(u, 30000); if (body != null) break; }
                catch (Exception e) { lastErr = e.Message; }
            }
            if (body == null) throw new Exception("无法获取版本清单: " + (lastErr ?? "未知错误"));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var list = new List<VersionEntry>();
            if (root.TryGetProperty("versions", out var versions))
            {
                foreach (var v in versions.EnumerateArray())
                {
                    var vid = v.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                    if (vid.Length == 0) continue;
                    var e = new VersionEntry
                    {
                        Id = vid,
                        Type = v.TryGetProperty("type", out var tp) ? tp.GetString() ?? "release" : "release",
                        ReleaseTime = v.TryGetProperty("releaseTime", out var rt) ? rt.GetString() ?? "" : "",
                        Url = v.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        Installed = IsInstalled(vid),
                    };
                    list.Add(e);
                }
            }
            return list;
        });
    }

    public static bool IsInstalled(string id)
    {
        return File.Exists(AppPaths.VersionJson(id)) &&
               File.Exists(AppPaths.VersionJar(id)) &&
               Directory.Exists(AppPaths.GameDir(id));
    }

    /// <summary>获取版本 JSON 并保存到 versions/&lt;id&gt;/&lt;id&gt;.json。</summary>
    public static async Task<JsonDocument> EnsureVersionJsonAsync(VersionEntry e)
    {
        return await Task.Run(() =>
        {
            var dest = AppPaths.VersionJson(e.Id);
            if (File.Exists(dest))
                return JsonDocument.Parse(File.ReadAllText(dest));
            if (string.IsNullOrEmpty(e.Url)) throw new Exception("版本缺少下载地址");
            Directory.CreateDirectory(AppPaths.VersionDir(e.Id));
            string? body;
            try { body = NovaKernel.HttpGet(e.Url, 60000); }
            catch (Exception ex) { body = null; LogService.Warn("版本 JSON 拉取失败: " + ex.Message); }
            if (body == null)
            {
                // 镜像兜底
                var mirror = BmclapiBase + "/version/" + e.Id + "/json";
                body = NovaKernel.HttpGet(mirror, 60000);
            }
            if (body == null) throw new Exception("无法获取版本 JSON: " + e.Id);
            File.WriteAllText(dest, body);
            return JsonDocument.Parse(body);
        });
    }

    public async Task<bool> InstallVersionAsync(VersionEntry e)
    {
        Phase?.Invoke("解析版本");
        var vj = await EnsureVersionJsonAsync(e);
        var id = e.Id;
        Directory.CreateDirectory(AppPaths.GameDir(id));   // 实例目录随安装创建，保证 IsInstalled 成立

        try
        {
            var root = vj.RootElement;

            // ---- 阶段 A：客户端 / 索引 / 库 / natives / 日志配置 ----
            var jobs = new List<DownloadJob>();

            // 客户端 jar
            if (root.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("client", out var client))
            {
                var dest = AppPaths.VersionJar(id);
                if (!File.Exists(dest))
                {
                    var j = new DownloadJob { Key = "client", Dest = dest };
                    j.Sha1 = client.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                    if (client.TryGetProperty("url", out var cu) && cu.GetString() is string curl) j.Urls.Add(curl);
                    if (SettingsService.S.UseBmclapi) j.Urls.Add($"{BmclapiBase}/version/{id}/client");
                    jobs.Add(j);
                }
            }

            // 资源索引
            if (root.TryGetProperty("assetIndex", out var ai) && ai.TryGetProperty("id", out var aiidProp))
            {
                var aiid = aiidProp.GetString() ?? "legacy";
                var dest = Path.Combine(AppPaths.Assets, "indexes", aiid + ".json");
                if (!File.Exists(dest))
                {
                    var j = new DownloadJob { Key = "assetIndex", Dest = dest };
                    j.Sha1 = ai.TryGetProperty("sha1", out var a1) ? a1.GetString() : null;
                    if (ai.TryGetProperty("url", out var au) && au.GetString() is string aurl) j.Urls.Add(aurl);
                    if (SettingsService.S.UseBmclapi) j.Urls.Add($"{BmclapiBase}/indexes/{aiid}.json");
                    jobs.Add(j);
                }
            }

            // 库文件 + natives
            var nativeJobs = new List<DownloadJob>();
            string nativesOs = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsMacOS() ? "osx" : "linux");
            string nativesArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => "x86_64",
            };
            if (root.TryGetProperty("libraries", out var libs))
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    var name = lib.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null) continue;
                    string? rel = null;
                    if (lib.TryGetProperty("downloads", out var ld))
                    {
                        if (ld.TryGetProperty("artifact", out var art))
                        {
                            rel = art.TryGetProperty("path", out var p) ? p.GetString() : null;
                            var dest = rel == null ? null : Path.Combine(AppPaths.Libraries, rel);
                            if (rel != null && dest != null && !File.Exists(dest))
                            {
                                var j = new DownloadJob { Key = "lib:" + name, Dest = dest };
                                j.Sha1 = art.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                                j.Size = art.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                                if (art.TryGetProperty("url", out var u) && u.GetString() is string url) j.Urls.Add(url);
                                if (SettingsService.S.UseBmclapi) j.Urls.Add($"{BmclapiBase}/maven/{rel}");
                                if (j.Urls.Count == 0) j.Urls.Add($"{MojangLibraries}/{rel}");
                                jobs.Add(j);
                            }
                        }
                        if (ld.TryGetProperty("classifiers", out var cf))
                        {
                            var ckey = $"natives-{nativesOs}-{nativesArch}";
                            if (cf.TryGetProperty(ckey, out var nativeArt))
                            {
                                var rel2 = nativeArt.TryGetProperty("path", out var p2) ? p2.GetString() : null;
                                if (rel2 != null)
                                {
                                    var dest = Path.Combine(AppPaths.Libraries, rel2);
                                    if (!File.Exists(dest))
                                    {
                                        var j = new DownloadJob { Key = "native:" + name, Dest = dest };
                                        j.Sha1 = nativeArt.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                                        if (nativeArt.TryGetProperty("url", out var u) && u.GetString() is string url) j.Urls.Add(url);
                                        if (SettingsService.S.UseBmclapi) j.Urls.Add($"{BmclapiBase}/maven/{rel2}");
                                        if (j.Urls.Count == 0) j.Urls.Add($"{MojangLibraries}/{rel2}");
                                        nativeJobs.Add(j);
                                    }
                                }
                            }
                        }
                    }
                    else if (SettingsService.S.UseMojang)
                    {
                        // 旧版本：只有 name，按 Maven 规则拼 URL
                        rel = MavenPathFromName(name);
                        var dest = Path.Combine(AppPaths.Libraries, rel);
                        if (!File.Exists(dest))
                        {
                            var j = new DownloadJob { Key = "lib:" + name, Dest = dest };
                            j.Urls.Add($"{MojangLibraries}/{rel}");
                            if (SettingsService.S.UseBmclapi) j.Urls.Add($"{BmclapiBase}/maven/{rel}");
                            jobs.Add(j);
                        }
                    }
                }
            }

            // 日志配置
            string? logConfigPath = null;
            if (root.TryGetProperty("logging", out var lg) && lg.TryGetProperty("client", out var lgclient) &&
                lgclient.TryGetProperty("file", out var lgfile))
            {
                var fileId = lgfile.TryGetProperty("id", out var fid) ? fid.GetString() ?? "client" : "client";
                logConfigPath = Path.Combine(AppPaths.VersionDir(id), "logs", fileId);
                if (!File.Exists(logConfigPath))
                {
                    var j = new DownloadJob { Key = "logging", Dest = logConfigPath };
                    j.Sha1 = lgfile.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                    if (lgfile.TryGetProperty("url", out var u) && u.GetString() is string url) j.Urls.Add(url);
                    jobs.Add(j);
                }
            }

            Phase?.Invoke("下载客户端与库");
            var phaseA = jobs.Concat(nativeJobs).ToList();
            if (phaseA.Count > 0)
            {
                var (ok, fail) = await DownloadService.DownloadAsync(phaseA, SettingsService.S.Workers,
                    new Progress<DownloadReport>(r => Progress?.Invoke("A", r.DoneCount, r.TotalCount, r.Finished)));
                if (fail > 0 && ok == 0) throw new Exception("客户端/库下载失败，请检查网络或更换下载源");
            }

            // 解压 natives
            if (nativeJobs.Count > 0)
            {
                Phase?.Invoke("解压原生库");
                var nativesDir = AppPaths.NativesDir(id);
                Directory.CreateDirectory(nativesDir);
                foreach (var j in nativeJobs)
                {
                    if (!File.Exists(j.Dest)) continue;
                    try
                    {
                        using var zf = ZipFile.OpenRead(j.Dest);
                        foreach (var entry in zf.Entries)
                        {
                            if (entry.FullName.EndsWith("/")) continue;
                            var target = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
                            try { entry.ExtractToFile(target, true); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn("natives 解压失败: " + j.Key + " " + ex.Message);
                    }
                }
            }

            // ---- 阶段 B：资源文件（assets） ----
            var indexId = root.TryGetProperty("assetIndex", out var ai2) && ai2.TryGetProperty("id", out var aip)
                ? aip.GetString() : "legacy";
            var indexFile = Path.Combine(AppPaths.Assets, "indexes", indexId + ".json");
            if (File.Exists(indexFile))
            {
                Phase?.Invoke("检查资源文件");
                using var idx = JsonDocument.Parse(File.ReadAllText(indexFile));
                var objects = idx.RootElement.TryGetProperty("objects", out var objs) ? objs : default;
                var assetJobs = new List<DownloadJob>();
                long totalBytes = 0;
                int pending = 0;
                if (objects.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in objects.EnumerateObject())
                    {
                        if (!kv.Value.TryGetProperty("hash", out var hProp)) continue;
                        var hash = hProp.GetString();
                        if (string.IsNullOrEmpty(hash)) continue;
                        var size = kv.Value.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        var dest = Path.Combine(AppPaths.Assets, "objects", hash[..2], hash);
                        if (File.Exists(dest) && new FileInfo(dest).Length == size) continue; // 秒过
                        var j = new DownloadJob
                        {
                            Key = "asset:" + hash[..8],
                            Dest = dest,
                            Size = size,
                            Sha1 = hash,
                        };
                        if (SettingsService.S.UseMojang) j.Urls.Add($"{MojangResources}/{hash[..2]}/{hash}");
                        if (SettingsService.S.UseBmclapi) j.Urls.Add($"{BmclapiBase}/assets/{hash}");
                        j.Urls.Add($"{MojangResources}/{hash[..2]}/{hash}"); // 兜底
                        assetJobs.Add(j);
                        totalBytes += size;
                        pending++;
                    }
                }
                if (pending > 0)
                {
                    Phase?.Invoke($"下载资源文件（{pending} 个，约 {totalBytes / 1024 / 1024} MB）");
                    var (ok, fail) = await DownloadService.DownloadAsync(assetJobs, SettingsService.S.Workers,
                        new Progress<DownloadReport>(r => Progress?.Invoke("B", r.DoneCount, r.TotalCount, r.Finished)));
                    if (fail > 0 && ok == 0) throw new Exception("资源文件下载失败，请检查网络");
                }
            }

            return true;
        }
        finally
        {
            vj.Dispose();
        }
    }

    public static string MavenPathFromName(string name)
    {
        var parts = name.Split(':');
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts.Length > 2 ? parts[2] : "";
        var classifier = "";
        var v = version;
        var at = version.IndexOf('@');
        if (at >= 0) { v = version[..at]; classifier = version[(at + 1)..]; }
        var file = artifact + "-" + v + (classifier.Length > 0 ? "-" + classifier : "") + ".jar";
        return $"{group}/{artifact}/{v}/{file}";
    }
}
