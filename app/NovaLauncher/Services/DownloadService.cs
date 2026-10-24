using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using NovaLauncher.Kernel;

namespace NovaLauncher.Services;

public sealed class DownloadJob
{
    public string Key { get; set; } = "";
    public List<string> Urls { get; set; } = new();
    public string Dest { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }   // 已知大小（用于进度估算），0 = 未知
}

public sealed class DownloadReport
{
    public int DoneCount { get; init; }
    public int TotalCount { get; init; }
    public long DoneBytes { get; init; }
    public long TotalBytes { get; init; }
    public bool Finished { get; init; }
}

/// <summary>多源并行下载：全部经 C 内核 nova_download_batch 执行。</summary>
public static class DownloadService
{
    private sealed class JobDto
    {
        public string key { get; set; } = "";
        public List<string> urls { get; set; } = new();
        public string dest { get; set; } = "";
        public string? sha1 { get; set; }
    }

    /// <summary>返回 (成功数, 失败数)。</summary>
    public static async Task<(int ok, int fail)> DownloadAsync(
        IReadOnlyList<DownloadJob> jobs, int workers,
        IProgress<DownloadReport>? progress = null)
    {
        if (jobs.Count == 0) { progress?.Report(new DownloadReport { TotalCount = 0, Finished = true }); return (0, 0); }

        var dtos = new List<JobDto>(jobs.Count);
        long totalBytes = 0;
        foreach (var j in jobs)
        {
            dtos.Add(new JobDto { key = j.Key, urls = j.Urls, dest = j.Dest, sha1 = j.Sha1 });
            totalBytes += j.Size;
        }
        var json = JsonSerializer.Serialize(dtos);

        var doneBytes = new Dictionary<string, long>();
        int done = 0;
        var tcs = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Task.Run(() =>
        {
            JsonElement result;
            try
            {
                result = NovaKernel.DownloadBatch(json, workers, 120000, (key, d, t, jobDone) =>
                {
                    if (jobDone)
                    {
                        lock (doneBytes)
                        {
                            done++;
                            var j = Find(jobs, key);
                            if (j != null) doneBytes[key] = j.Size > 0 ? j.Size : d;
                        }
                    }
                    progress?.Report(new DownloadReport
                    {
                        DoneCount = done,
                        TotalCount = jobs.Count,
                        DoneBytes = Sum(doneBytes),
                        TotalBytes = totalBytes,
                    });
                });
            }
            catch (Exception e)
            {
                LogService.Error("内核下载失败: " + e.Message);
                tcs.TrySetResult((0, jobs.Count));
                return;
            }

            int ok = 0, fail = 0;
            foreach (var item in result.EnumerateArray())
            {
                if (item.TryGetProperty("ok", out var okProp) && okProp.GetBoolean()) ok++; else fail++;
            }
            tcs.TrySetResult((ok, fail));
        });

        var (o, f) = await tcs.Task;
        progress?.Report(new DownloadReport { DoneCount = o + f, TotalCount = jobs.Count, DoneBytes = totalBytes, TotalBytes = totalBytes, Finished = true });
        return (o, f);
    }

    private static DownloadJob? Find(IReadOnlyList<DownloadJob> jobs, string key)
    {
        foreach (var j in jobs) if (j.Key == key) return j;
        return null;
    }

    private static long Sum(Dictionary<string, long> d)
    {
        long s = 0;
        foreach (var kv in d) s += kv.Value;
        return s;
    }

    /// <summary>单文件下载（带请求头，用于 CurseForge API / AI 等需要鉴权的资源）。</summary>
    public static async Task<bool> DownloadFileWithHeadersAsync(string url, string dest, string? sha1, string headers)
    {
        return await Task.Run(() =>
        {
            try
            {
                return NovaKernel.DownloadFileEx(url, dest, sha1, headers) == 0;
            }
            catch (Exception e)
            {
                LogService.Error("下载失败: " + e.Message);
                return false;
            }
        });
    }
}
