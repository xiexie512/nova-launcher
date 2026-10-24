using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NovaLauncher.Kernel;

namespace NovaLauncher.Services;

/// <summary>Modrinth 搜索结果条目（模组 / 整合包共用）。</summary>
public sealed class ModrinthItem
{
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string ProjectType { get; init; } = "mod"; // mod / modpack
    public long Downloads { get; init; }
    public List<string> Categories { get; init; } = new();
    public string DownloadsText => Downloads >= 1_000_000 ? $"{Downloads / 1_000_000.0:F1}M" : Downloads >= 1_000 ? $"{Downloads / 1_000.0:F0}K" : Downloads.ToString();
}

/// <summary>Modrinth 官方 API 客户端：模组 / 整合包搜索与文件下载。</summary>
public static class ModrinthClient
{
    private const string Base = "https://api.modrinth.com/v2";
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NovaLauncher/1.0 (Nova MC Launcher)");
        c.Timeout = TimeSpan.FromSeconds(60);
        return c;
    }

    /// <summary>搜索模组（可按 MC 版本 / 加载器过滤）。</summary>
    public static async Task<List<ModrinthItem>> SearchModsAsync(string query, string? mcVersion, string? loader, int limit = 20)
    {
        return await SearchAsync(query, "mod", mcVersion, loader, limit);
    }

    /// <summary>搜索整合包（可按 MC 版本 / 加载器过滤）。</summary>
    public static async Task<List<ModrinthItem>> SearchModpacksAsync(string query, string? mcVersion, string? loader, int limit = 20)
    {
        return await SearchAsync(query, "modpack", mcVersion, loader, limit);
    }

    private static async Task<List<ModrinthItem>> SearchAsync(string query, string type, string? mcVersion, string? loader, int limit)
    {
        return await Task.Run(() =>
        {
            var facets = new List<object> { new object[] { "project_type:" + type } };
            if (!string.IsNullOrWhiteSpace(mcVersion)) facets.Add(new object[] { "versions:" + mcVersion });
            if (!string.IsNullOrWhiteSpace(loader)) facets.Add(new object[] { "categories:" + loader });
            var facetsJson = JsonSerializer.Serialize(facets);

            var url = $"{Base}/search?query={Uri.EscapeDataString(query)}&limit={limit}&facets={Uri.EscapeDataString(facetsJson)}";
            string body;
            try
            {
                body = NovaKernel.HttpGet(url, 30000) ?? throw new Exception("Modrinth 无响应");
            }
            catch (Exception e)
            {
                throw new Exception("搜索失败: " + e.Message);
            }

            using var doc = JsonDocument.Parse(body);
            var list = new List<ModrinthItem>();
            if (!doc.RootElement.TryGetProperty("hits", out var hits)) return list;
            foreach (var h in hits.EnumerateArray())
            {
                list.Add(new ModrinthItem
                {
                    ProjectId = Get(h, "project_id"),
                    Slug = Get(h, "slug"),
                    Title = Get(h, "title"),
                    Description = Get(h, "description"),
                    Author = Get(h, "author"),
                    ProjectType = Get(h, "project_type"),
                    Downloads = h.TryGetProperty("downloads", out var d) ? d.GetInt64() : 0,
                    Categories = h.TryGetProperty("categories", out var c) ? c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>(),
                });
            }
            return list;
        });
    }

    private static string Get(JsonElement e, string key)
        => e.TryGetProperty(key, out var p) ? p.GetString() ?? "" : "";

    /// <summary>取项目最新、且匹配 MC 版本 / 加载器的文件（模组 .jar / 整合包 .mrpack）。</summary>
    public static Task<ModrinthFile> GetLatestFileAsync(string projectIdOrSlug, string? mcVersion, string? loader, string extension)
    {
        return Task.Run(() =>
        {
            var url = $"{Base}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
            if (!string.IsNullOrEmpty(mcVersion)) url += $"?game_versions={Uri.EscapeDataString("[\"" + mcVersion + "\"]")}";
            if (!string.IsNullOrEmpty(loader)) url += (url.Contains('?') ? "&" : "?") + $"loaders={Uri.EscapeDataString("[\"" + loader + "\"]")}";

            string body;
            try
            {
                body = NovaKernel.HttpGet(url, 30000) ?? throw new Exception("无响应");
            }
            catch (Exception e)
            {
                throw new Exception("获取版本列表失败: " + e.Message);
            }

            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                throw new Exception("该项目没有可用版本");

            // 遍历（API 已按发布时间倒序），挑第一个带目标扩展名文件的版本
            foreach (var v in arr.EnumerateArray())
            {
                if (!v.TryGetProperty("files", out var files)) continue;
                foreach (var f in files.EnumerateArray())
                {
                    var fn = f.TryGetProperty("filename", out var fnp) ? fnp.GetString() ?? "" : "";
                    if (!fn.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    var fileUrl = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (fileUrl.Length == 0) continue;
                    return new ModrinthFile
                    {
                        FileName = fn,
                        Url = fileUrl,
                        VersionNumber = v.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : "",
                        GameVersion = v.TryGetProperty("game_versions", out var gv) && gv.GetArrayLength() > 0 ? gv[0].GetString() ?? "" : "",
                        Loader = v.TryGetProperty("loaders", out var lv) && lv.GetArrayLength() > 0 ? lv[0].GetString() ?? "" : "",
                    };
                }
            }
            throw new Exception("该项目没有 " + extension + " 文件（可能不支持当前过滤条件）");
        });
    }

    /// <summary>带进度下载文件。</summary>
    public static async Task DownloadAsync(string url, string dest, IProgress<string>? progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}: {url}");
        long total = resp.Content.Headers.ContentLength ?? 0;
        using var fs = File.Create(dest);
        using var src = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            await fs.WriteAsync(buf, 0, n);
            done += n;
            if (total > 0) progress?.Report($"{done / 1024 / 1024.0:F1} MB / {total / 1024 / 1024.0:F1} MB");
        }
    }
}

/// <summary>Modrinth 版本文件（选中的下载目标）。</summary>
public sealed class ModrinthFile
{
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "";
}
