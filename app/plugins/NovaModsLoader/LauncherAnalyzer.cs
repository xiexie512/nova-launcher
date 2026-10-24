using System.Reflection;
using System.Text;
using NovaLauncher.PluginApi;

namespace NovaModsLoader;

/// <summary>
/// 「反向编译」Nova 启动器：插件运行在启动器进程内，用反射读取 NovaLauncher 程序集的
/// 结构（命名空间、服务、视图模型、启动流水线、插件接口面），导出可读分析报告。
/// </summary>
public static class LauncherAnalyzer
{
    public static string Run(ILauncherApi api, Action<string> status)
    {
        status("开始反编译分析 Nova 启动器…");

        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "NovaLauncher")
            ?? throw new Exception("未找到 NovaLauncher 程序集（本插件应运行在启动器进程内）");

        var sb = new StringBuilder();
        sb.AppendLine("# Nova 启动器结构分析（反射反编译）");
        sb.AppendLine();
        sb.AppendLine($"程序集：{asm.GetName().Name} v{asm.GetName().Version}");
        sb.AppendLine($"运行时：{Environment.Version}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        var types = asm.GetTypes().Where(t => t.IsPublic || t.Namespace?.StartsWith("NovaLauncher") == true)
            .OrderBy(t => t.FullName).ToList();
        sb.AppendLine($"公开类型总数：{types.Count}");
        sb.AppendLine();

        // 按命名空间分组
        foreach (var group in types.Where(t => t.Namespace != null).GroupBy(t => t.Namespace).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key}（{group.Count()} 个类型）");
            foreach (var t in group.OrderBy(t => t.Name))
            {
                var kind = t.IsInterface ? "接口" : t.IsEnum ? "枚举" : t.IsAbstract && t.IsSealed ? "静态类" : t.IsClass ? "类" : "结构";
                sb.AppendLine($"- [{kind}] {t.Name}");
            }
            sb.AppendLine();
        }

        // 启动流水线：LaunchService 的关键方法
        var launch = types.FirstOrDefault(t => t.Name == "LaunchService");
        if (launch != null)
        {
            sb.AppendLine("## 游戏启动流水线（LaunchService）");
            foreach (var m in launch.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName))
                sb.AppendLine($"- 方法：{m.Name}({(string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)))})");
            foreach (var p in launch.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine($"- 属性：{p.PropertyType.Name} {p.Name}");
            sb.AppendLine();
        }

        // 插件 API 面
        sb.AppendLine("## 插件接口面（PluginApi）");
        foreach (var t in types.Where(t => t.Namespace == "NovaLauncher.PluginApi"))
            sb.AppendLine($"- {t.FullName}");
        sb.AppendLine();

        var reportDir = Path.Combine(api.LauncherHome, "exports");
        Directory.CreateDirectory(reportDir);
        var report = Path.Combine(reportDir, "nova-launcher-decompile.md");
        File.WriteAllText(report, sb.ToString());

        status($"反编译完成：分析 {types.Count} 个类型，报告已导出到\n{report}\n\n（摘要见左侧报告路径）");
        return $"导出反编译报告 → {report}（{types.Count} 个类型）";
    }
}
