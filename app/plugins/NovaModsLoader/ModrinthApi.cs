using System.Text.Json.Nodes;
using NovaLauncher.PluginApi;

namespace NovaModsLoader;

/// <summary>
/// 原版 Modrinth 官方 API（api.modrinth.com/v2）：
/// 搜索模组、获取最新匹配当前 MC 版本的构建、下载 .jar 到目标实例的 mods 目录。
/// </summary>
public static class ModrinthApi
{
    private const string Base = "https://api.modrinth.com/v2";

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NovaModsLoader/1.0 (Nova Launcher)");
        c.Timeout = TimeSpan.FromSeconds(60);
        return c;
    }

    private static string? _lastSlug;

    /// <summary>按关键词搜索当前 MC 版本的模组，记住结果并把首个可用 slug 作为待下载目标。</summary>
    public static string SearchAndRemember(ILauncherApi api, Action<string> status)
    {
        var mc = api.GetSetting("mcVersion", "1.21.1");
        var q = api.GetSetting("modQuery", "popular");
        status($"Modrinth 搜索「{q}」（MC {mc}）…");

        var url = $"{Base}/search?query={Uri.EscapeDataString(q)}&limit=12&facets=" +
                  Uri.EscapeDataString($"[[\"versions:{mc}\"]]");
        using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new Exception($"Modrinth HTTP {(int)resp.StatusCode}");
        var root = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();

        var lines = new List<string>();
        string? first = null;
        var hits = root["hits"]?.AsArray() ?? new JsonArray();
        foreach (var h in hits.Take(12))
        {
            var slug = h!["slug"]?.GetValue<string>() ?? "";
            var title = h["title"]?.GetValue<string>() ?? "";
            var downloads = h["downloads"]?.GetValue<long>() ?? 0;
            if (first == null && !string.IsNullOrEmpty(slug)) first = slug;
            lines.Add($"  {title}  (slug: {slug})  下载 {downloads:N0}");
        }
        if (lines.Count == 0) throw new Exception("没有搜到匹配该 MC 版本的模组");

        _lastSlug = first;
        if (first != null) api.SetSetting("modSlug", first);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"搜索结果（{hits.Count} 个，MC {mc}）：");
        sb.AppendLine(string.Join("\n", lines.Take(8)));
        sb.AppendLine();
        sb.AppendLine($"已记住待下载 slug：{first}");
        sb.AppendLine("改在设置项 NovaModsLoader.modSlug，然后点「下载上次搜索的模组」。");
        status(sb.ToString());
        return $"搜索完成：{hits.Count} 个结果，待下载 {first}";
    }

    /// <summary>下载待下载 slug 的最新、匹配当前 MC 版本的模组到目标实例 mods 目录。</summary>
    public static string DownloadLast(ILauncherApi api, Action<string> status)
    {
        var mc = api.GetSetting("mcVersion", "1.21.1");
        var slug = api.GetSetting("modSlug", _lastSlug ?? "");
        if (string.IsNullOrEmpty(slug)) throw new Exception("还没有待下载的模组，请先搜索（或设置 NovaModsLoader.modSlug）");

        status($"获取模组 {slug} 的版本…");
        using var resp = Http.GetAsync($"{Base}/project/{slug}/version").GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new Exception($"Modrinth HTTP {(int)resp.StatusCode}（项目 {slug}）");
        var versions = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsArray();

        JsonNode? pick = null;
        foreach (var v in versions)
        {
            var gv = v!["game_versions"]?.AsArray();
            if (gv == null) continue;
            var ok = gv.Any(g => g!.GetValue<string>() == mc || g.GetValue<string>().StartsWith(mc.Split('.')[0] + "."));
            if (ok) { pick = v; break; }
        }
        pick ??= versions.FirstOrDefault();

        if (pick == null) throw new Exception($"模组 {slug} 没有可用版本");
        var file = pick["files"]?.AsArray().FirstOrDefault(f => (f!["filename"]?.GetValue<string>() ?? "").EndsWith(".jar"));
        if (file == null) throw new Exception("模组没有 .jar 文件");
        var fileUrl = file["url"]!.GetValue<string>();
        var fileName = file["filename"]!.GetValue<string>();
        var verName = pick["version_number"]?.GetValue<string>() ?? "?";

        var modsDir = Path.Combine(api.GameRoot, "versions", api.GetSetting("modsInstance", "fabric-loader-" + mc), "instance", "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, fileName);
        status($"下载 {fileName}（{verName}）到 {modsDir}…");

        using var dl = Http.GetAsync(fileUrl).GetAwaiter().GetResult();
        dl.EnsureSuccessStatusCode();
        using var fs = File.Create(dest);
        using var src = dl.Content.ReadAsStream();
        src.CopyTo(fs);
        status($"已下载到 {modsDir}：{fileName}");
        return $"下载 {fileName}（{verName}）→ {modsDir}";
    }
}
