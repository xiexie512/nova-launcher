using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NovaLauncher.Kernel;

/// <summary>对自研 C 内核 (nova_kernel) 的 P/Invoke 封装。</summary>
internal static class NovaKernel
{
    private const string Lib = "nova_kernel";

    static NovaKernel()
    {
        // 解析内核动态库：兼容 dev 构建（Native/ 子目录）与发布布局（输出根目录）
        NativeLibrary.SetDllImportResolver(typeof(NovaKernel).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "nova_kernel.dylib"),
                Path.Combine(AppContext.BaseDirectory, "libnova_kernel.dylib"),
                Path.Combine(AppContext.BaseDirectory, "Native", "libnova_kernel.dylib"),
                Path.Combine(AppContext.BaseDirectory, "Native", "nova_kernel.dylib"),
                Path.Combine(AppContext.BaseDirectory, "nova_kernel.dll"),
                Path.Combine(AppContext.BaseDirectory, "Native", "nova_kernel.dll"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return NativeLibrary.Load(c);
            return IntPtr.Zero;
        });
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProgressFn(int jobId, [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        long downloaded, long total, int jobDone, int batchDone, IntPtr user);

    private static ProgressFn? _progressHolder; // 防止被 GC 回收

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nova_free_string(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_http_get([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_http_post([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_http_get_ex([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extraHeaders, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_http_post_ex([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extraHeaders, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nova_download_file_ex([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dest, [MarshalAs(UnmanagedType.LPUTF8Str)] string sha1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extraHeaders, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_download_batch([MarshalAs(UnmanagedType.LPUTF8Str)] string jobsJson,
        int workers, int timeoutMs, ProgressFn cb, IntPtr user, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_build_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string reqJson,
        out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_offline_uuid([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_yggdrasil_login(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string clientToken, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_microsoft_device_code(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string clientId, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_microsoft_poll_token(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string clientId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceCode, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_microsoft_finish(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string accessToken, int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_java_find([MarshalAs(UnmanagedType.LPUTF8Str)] string dirsJson,
        out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nova_java_check([MarshalAs(UnmanagedType.LPUTF8Str)] string javaPath,
        out IntPtr err);

    public static string Version => Take(nova_version()) ?? "?";

    private static string? Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { nova_free_string(p); }
    }

    private static string? Error(ref IntPtr err)
    {
        var s = Take(err);
        err = IntPtr.Zero;
        return s;
    }

    public static string? HttpGet(string url, int timeoutMs = 30000)
    {
        var err = IntPtr.Zero;
        var p = nova_http_get(url, timeoutMs, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "http_get failed");
        return Take(p);
    }

    public static string? HttpPost(string url, string body, string contentType = "application/json", int timeoutMs = 30000)
    {
        var err = IntPtr.Zero;
        var p = nova_http_post(url, body, contentType, timeoutMs, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "http_post failed");
        return Take(p);
    }

    public static string? HttpGetEx(string url, string extraHeaders, int timeoutMs = 30000)
    {
        var err = IntPtr.Zero;
        var p = nova_http_get_ex(url, extraHeaders, timeoutMs, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "http_get_ex failed");
        return Take(p);
    }

    public static string? HttpPostEx(string url, string body, string contentType, string extraHeaders, int timeoutMs = 30000)
    {
        var err = IntPtr.Zero;
        var p = nova_http_post_ex(url, body, contentType, extraHeaders, timeoutMs, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "http_post_ex failed");
        return Take(p);
    }

    public static int DownloadFileEx(string url, string dest, string? sha1, string extraHeaders, int timeoutMs = 120000)
    {
        var err = IntPtr.Zero;
        return nova_download_file_ex(url, dest, sha1 ?? "", extraHeaders, timeoutMs, out err);
    }

    /// <summary>多源并行下载。progress 回调在调用线程上触发。</summary>
    public static JsonElement DownloadBatch(string jobsJson, int workers, int timeoutMs, Action<string, long, long, bool>? progress)
    {
        var err = IntPtr.Zero;
        ProgressFn cb = (jobId, key, downloaded, total, jobDone, batchDone, user) =>
            progress?.Invoke(key, downloaded, total, jobDone != 0);
        _progressHolder = cb;
        var p = nova_download_batch(jobsJson, workers, timeoutMs, cb, IntPtr.Zero, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "download_batch failed");
        var s = Take(p) ?? "[]";
        try { return JsonDocument.Parse(s).RootElement.Clone(); }
        catch (Exception e) { throw new KernelException("download_batch bad result: " + e.Message); }
    }

    public static string BuildLaunch(string reqJson)
    {
        var err = IntPtr.Zero;
        var p = nova_build_launch(reqJson, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "build_launch failed");
        return Take(p) ?? "[]";
    }

    public static string OfflineUuid(string name)
    {
        var err = IntPtr.Zero;
        var p = nova_offline_uuid(name, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "offline uuid failed");
        return Take(p) ?? "";
    }

    public static JsonElement YggdrasilLogin(string server, string username, string password, string clientToken)
    {
        var err = IntPtr.Zero;
        var p = nova_yggdrasil_login(server, username, password, clientToken, 30000, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "yggdrasil login failed");
        var s = Take(p)!;
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    public static JsonElement MicrosoftDeviceCode(string clientId)
    {
        var err = IntPtr.Zero;
        var p = nova_microsoft_device_code(clientId, 30000, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "device code failed");
        return JsonDocument.Parse(Take(p)!).RootElement.Clone();
    }

    public static JsonElement MicrosoftPollToken(string clientId, string deviceCode)
    {
        var err = IntPtr.Zero;
        var p = nova_microsoft_poll_token(clientId, deviceCode, 30000, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "poll token failed");
        return JsonDocument.Parse(Take(p)!).RootElement.Clone();
    }

    public static JsonElement MicrosoftFinish(string accessToken)
    {
        var err = IntPtr.Zero;
        var p = nova_microsoft_finish(accessToken, 30000, out err);
        if (p == IntPtr.Zero) throw new KernelException(Error(ref err) ?? "ms finish failed");
        return JsonDocument.Parse(Take(p)!).RootElement.Clone();
    }

    public static string? JavaFind(string dirsJson)
    {
        var err = IntPtr.Zero;
        var p = nova_java_find(dirsJson, out err);
        return Take(p);
    }

    public static bool JavaCheck(string path)
    {
        var err = IntPtr.Zero;
        return nova_java_check(path, out err) == 0;
    }
}

internal sealed class KernelException : Exception
{
    public KernelException(string message) : base(message) { }
}
