using NovaLauncher.PluginApi;

namespace HelloPlugin;

/// <summary>
/// 示例插件：展示插件如何「修改整个启动器」——
/// 注册主页卡片、注册侧栏页面、持久化插件设置、订阅启动事件。
/// </summary>
public sealed class HelloPlugin : ILauncherPlugin
{
    private ILauncherApi? _api;

    public string Name => "Hello Nova";
    public string Version => "1.1.0";
    public string Author => "NovaLauncher";
    public string Description => "示例插件：注册主页卡片与插件页，订阅启动事件";

    public void OnLoad(ILauncherApi api)
    {
        _api = api;
        api.Log("Hello Nova 插件已加载！本插件由你亲手制作 🎉");

        // ① 在主页注册一张可自由拖动的卡片
        api.RegisterHomeCard(new HomeCardInfo
        {
            Id = "hello-tip",
            Title = "来自插件的问候",
            Icon = "👋",
            Body = "我是 Hello Nova 插件注册的主页卡片。你可以拖着我随便放，还能在「自定义主页」里关掉我。想写自己的插件？把 dll 丢进 plugins 目录即可。",
        });

        // ② 在侧栏注册一个新页面
        api.RegisterPage(new PluginPageInfo
        {
            Key = "hello-about",
            Title = "Hello Nova",
            Icon = "👋",
            Body = "这是一个由插件注册的页面，证明插件可以扩展整个启动器 UI。\n\n"
                 + "插件能力一览：\n"
                 + "· RegisterHomeCard —— 主页卡片\n"
                 + "· RegisterPage —— 侧栏新页面\n"
                 + "· GetSetting / SetSetting —— 持久化插件设置\n"
                 + "· LauncherEvents —— 订阅游戏启动 / 日志 / 下载事件\n"
                 + "· api.Notify / api.Log —— 通知与日志\n\n"
                 + $"当前启动计数：{api.GetSetting("launchCount", "0")}",
        });

        // ③ 订阅启动事件：统计启动次数（持久化到启动器设置）
        LauncherEvents.GameLaunching += info =>
            api.Log($"检测到启动事件：{info.VersionId}（{info.AccountName}，内存 {info.MemoryMb}MB）");
        LauncherEvents.GameLaunched += info =>
        {
            api.Notify("游戏已启动", info.VersionId + " 启动完成，祝玩得开心！");
            var n = int.TryParse(api.GetSetting("launchCount", "0"), out var c) ? c + 1 : 1;
            api.SetSetting("launchCount", n.ToString());
            api.Log($"本插件累计记录启动 {n} 次");
        };
        LauncherEvents.ConsoleLine += line =>
        {
            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                api.Log("[游戏错误] " + line);
        };
    }

    public void OnUnload() { }
}
