using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NovaLauncher.Kernel;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

/// <summary>Java 运行时检测与自动安装（Adoptium Temurin）。</summary>
public static class JavaService
{
    public static List<string> CandidatePaths()
    {
        var list = new List<string>();
        var s = SettingsService.S;
        if (!string.IsNullOrWhiteSpace(s.JavaPath)) list.Add(s.JavaPath);

        var envJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(envJavaHome))
        {
            list.Add(Path.Combine(envJavaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            list.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "java.exe" : "java"));
        }

        // 常用安装位置
        if (OperatingSystem.IsMacOS())
        {
            list.Add("/usr/bin/java");
            list.Add("/usr/local/bin/java");
            list.Add("/opt/homebrew/bin/java");
            foreach (var vm in SafeGlob("/Library/Java/JavaVirtualMachines/*/Contents/Home/bin/java"))
                list.Add(vm);
            foreach (var vm in SafeGlob("/opt/homebrew/opt/openjdk*/bin/java"))
                list.Add(vm);
            foreach (var vm in SafeGlob("/opt/homebrew/Cellar/openjdk*/current/bin/java"))
                list.Add(vm);
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var vm in SafeGlob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "*", "bin", "java.exe")))
                list.Add(vm);
            foreach (var vm in SafeGlob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium", "*", "bin", "java.exe")))
                list.Add(vm);
            foreach (var vm in SafeGlob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "jdk-*", "bin", "java.exe")))
                list.Add(vm);
        }
        return list.Distinct().ToList();
    }

    private static IEnumerable<string> SafeGlob(string pattern)
    {
        foreach (var p in Expand(pattern)) yield return p;
    }

    private static IEnumerable<string> Expand(string pattern)
    {
        var sep = Path.DirectorySeparatorChar;
        var parts = pattern.Split(sep);
        var current = new List<string> { "" };
        foreach (var part in parts)
        {
            if (part.Length == 0) { current = current.Select(c => c + sep).ToList(); continue; }
            var next = new List<string>();
            foreach (var c in current)
            {
                var dir = c.Length == 0 ? sep.ToString() : c.TrimEnd(sep);
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> matches;
                if (part.Contains('*'))
                {
                    var wild = part.Replace("**", "*");
                    try { matches = Directory.GetFileSystemEntries(dir, wild); }
                    catch { continue; }
                }
                else
                {
                    matches = new[] { Path.Combine(dir, part) };
                }
                foreach (var m in matches) next.Add(m);
            }
            current = next;
        }
        return current;
    }

    /// <summary>查找可用 Java（经 C 内核校验）。</summary>
    public static string? FindJava()
    {
        var candidates = CandidatePaths().Where(p => File.Exists(p)).ToList();
        if (candidates.Count == 0) return null;
        var json = JsonSerializer.Serialize(candidates);
        try { return NovaKernel.JavaFind(json); }
        catch (Exception e) { LogService.Warn("Java 检测失败: " + e.Message); return null; }
    }

    /// <summary>自动下载指定大版本的 Temurin JDK 到 home/jdk，返回 java 可执行文件路径。</summary>
    public static async Task<string?> DownloadJavaAsync(int major, IProgress<string>? progress)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsMacOS() ? "mac" : "linux");
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x64";
        var api = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?os={os}&architecture={arch}&image_type=jdk";

        progress?.Report("查询 Java 下载地址…");
        var body = NovaKernel.HttpGet(api, 30000);
        if (body == null) throw new Exception("无法获取 Java 下载信息");
        using var doc = JsonDocument.Parse(body);
        var pkg = doc.RootElement[0];
        var link = pkg.GetProperty("package").GetProperty("link").GetString();
        var name = pkg.GetProperty("package").GetProperty("name").GetString() ?? "jdk.tar.gz";
        if (link == null) throw new Exception("Java 下载链接缺失");

        var destDir = Path.Combine(AppPaths.Home, "jdk", $"temurin-{major}");
        Directory.CreateDirectory(destDir);
        var tmpFile = Path.Combine(destDir, name);
        progress?.Report("下载 Java 运行时…");
        var job = new DownloadJob { Key = "java", Dest = tmpFile };
        job.Urls.Add(link);
        var (ok, _) = await DownloadService.DownloadAsync(new[] { job }, 1, null);
        if (ok != 1) throw new Exception("Java 下载失败");

        progress?.Report("解压 Java 运行时…");
        if (name.EndsWith(".zip")) ZipFile.ExtractToDirectory(tmpFile, destDir, true);
        else if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz"))
        {
            using var fs = File.OpenRead(tmpFile);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                var target = Path.Combine(destDir, entry.Name);
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.DataStream?.CopyTo(File.Create(target));
            }
        }
        else throw new Exception("不支持的 Java 压缩格式: " + name);
        File.Delete(tmpFile);

        var root = Directory.GetDirectories(destDir).FirstOrDefault() ?? destDir;
        var java = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (!File.Exists(java)) throw new Exception("Java 安装目录结构异常");
        SettingsService.S.JavaPath = java;
        SettingsService.Save();
        return java;
    }
}
