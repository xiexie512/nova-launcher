using System.Reflection;
using System.Runtime.InteropServices;

namespace PyCExample;

/// <summary>
/// 插件侧对启动器自研 C 内核 (nova_kernel) 的 P/Invoke 封装。
/// 与主程序 NovaKernel.cs 同一模式：通过 DllImportResolver 在 AppContext.BaseDirectory
/// 下查找 libnova_kernel.dylib / nova_kernel.dll；也可用环境变量 NOVA_KERNEL_LIB 显式指定。
/// 所有返回的 char* 均由内核 malloc 分配，读取后必须用 nova_free_string 释放。
/// </summary>
internal static class PycKernel
{
    private const string Lib = "nova_kernel";

    /// <summary>实际加载到的内核库绝对路径（供插件页展示）。</summary>
    public static string KernelLibPath { get; private set; } = "";

    static PycKernel()
    {
        NativeLibrary.SetDllImportResolver(typeof(PycKernel).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib) return IntPtr.Zero;

        // 测试 / 特殊部署：通过环境变量直接指定内核库绝对路径
        var fromEnv = Environment.GetEnvironmentVariable("NOVA_KERNEL_LIB");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            KernelLibPath = fromEnv;
            return NativeLibrary.Load(fromEnv);
        }

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
            if (File.Exists(c))
            {
                KernelLibPath = c;
                return NativeLibrary.Load(c);
            }
        return IntPtr.Zero;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nova_free_string(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_http_get([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        int timeoutMs, out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nova_offline_uuid([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out IntPtr err);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nova_java_check([MarshalAs(UnmanagedType.LPUTF8Str)] string javaPath,
        out IntPtr err);

    /// <summary>读回内核字符串并释放（内核 malloc 分配）。</summary>
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

    /// <summary>内核版本（失败时返回错误说明而不是抛异常，便于插件页展示）。</summary>
    public static string Version
    {
        get
        {
            try { return Take(nova_version()) ?? "?"; }
            catch (Exception e) { return "内核未加载: " + e.Message; }
        }
    }

    /// <summary>离线 UUID：MD5("OfflinePlayer:"+name) v3，格式 8-4-4-4-12。</summary>
    public static string OfflineUuid(string name)
    {
        var err = IntPtr.Zero;
        var p = nova_offline_uuid(name, out err);
        if (p == IntPtr.Zero) throw new InvalidOperationException(Error(ref err) ?? "offline_uuid failed");
        return Take(p) ?? "";
    }

    /// <summary>用 C 内核发 HTTP GET，返回响应体（UTF-8）。</summary>
    public static string HttpGet(string url, int timeoutMs = 15000)
    {
        var err = IntPtr.Zero;
        var p = nova_http_get(url, timeoutMs, out err);
        if (p == IntPtr.Zero) throw new InvalidOperationException(Error(ref err) ?? "http_get failed");
        return Take(p) ?? "";
    }

    /// <summary>校验 Java 路径存在且可执行（0 = 有效）。</summary>
    public static bool JavaCheck(string javaPath)
    {
        var err = IntPtr.Zero;
        return nova_java_check(javaPath, out err) == 0;
    }
}
