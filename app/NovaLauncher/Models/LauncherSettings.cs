using System.Collections.Generic;

namespace NovaLauncher.Models;

public sealed class LauncherSettings
{
    // ===== 个性化 · 外观 =====
    /// <summary>主题：0=跟随系统，1=浅色，2=深色。</summary>
    public int Theme { get; set; } = 2;
    /// <summary>强调色（如 #3FA54B），用于主按钮、导航选中态等。</summary>
    public string AccentColor { get; set; } = "#3FA54B";

    // ===== 个性化 · 账号与默认服务器 =====
    /// <summary>离线模式默认用户名（空=自动生成 Steve+随机）。</summary>
    public string OfflineUsername { get; set; } = "";
    /// <summary>默认服务器地址 ip:port（启动后自动进服时使用）。</summary>
    public string DefaultServer { get; set; } = "";
    /// <summary>启动游戏后自动加入默认服务器。</summary>
    public bool AutoJoinServer { get; set; }

    public int MemoryMb { get; set; } = 2048;
    public int Width { get; set; }        // 0 = 游戏默认
    public int Height { get; set; }
    public string JavaPath { get; set; } = "";   // 空 = 自动检测
    public int Workers { get; set; } = 8;
    public List<string> ExtraJvmArgs { get; set; } = new();
    public bool UseBmclapi { get; set; } = true;
    public bool UseMojang { get; set; } = true;
    public string MicrosoftClientId { get; set; } = "";
    public string CurseForgeKey { get; set; } = "";
    public string DefaultVersion { get; set; } = "";
    public List<GameAccount> Accounts { get; set; } = new();
    public string AiEndpoint { get; set; } = "";
    public string AiKey { get; set; } = "";
    public string AiModel { get; set; } = "deepseek-chat";

    /// <summary>ZeroTier Central 个人 API Token（后台管理成员用，my.zerotier.com → Account 生成）。</summary>
    public string ZeroTierToken { get; set; } = "";

    // 主页自定义
    public List<ServerEntry> Servers { get; set; } = new();
    public bool HomeShowInstances { get; set; } = true;
    public bool HomeShowSaves { get; set; } = true;
    public bool HomeShowServers { get; set; } = true;
    public bool HomeShowRooms { get; set; } = true;
    public bool HomeShowLan { get; set; } = true;
    public bool HomeShowVersions { get; set; } = true;
    public bool HomeShowPluginCards { get; set; } = true;
    /// <summary>主页可拖动卡片位置：cardId -> "x%,y%"（相对卡片容器）。</summary>
    public Dictionary<string, string> HomeCardPos { get; set; } = new();
    /// <summary>首页插件网格布局：每个已放置小组件的类型/位置/尺寸。</summary>
    public List<WidgetLayoutEntry> HomeWidgetLayout { get; set; } = new();
    /// <summary>插件私有设置：key = "插件名.设置键"。</summary>
    public Dictionary<string, string> PluginSettings { get; set; } = new();

    /// <summary>实例最近游玩时间：instanceId -> unix 秒（用于「游玩于 / 上次游玩于」）。</summary>
    public Dictionary<string, long> LastPlayedTimes { get; set; } = new();
    /// <summary>按日期计的游玩局数：yyyy-MM-dd -> 次数（用于主页日历当天记录）。</summary>
    public Dictionary<string, int> PlayLog { get; set; } = new();
    /// <summary>收藏的实例（主页「固定的实例」）。</summary>
    public List<string> PinnedInstances { get; set; } = new();
    /// <summary>收藏的世界（主页「固定的世界」）。</summary>
    public List<string> PinnedWorlds { get; set; } = new();

    public GameAccount? SelectedAccount
    {
        get
        {
            foreach (var a in Accounts) if (a.Selected) return a;
            return Accounts.Count > 0 ? Accounts[0] : null;
        }
    }
}
