namespace NovaLauncher.Models;

/// <summary>主页「我的服务器」条目。</summary>
public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";   // ip:port
    public string Version { get; set; } = "";   // 备注的 MC 版本（可选）
    public string Note { get; set; } = "";

    /// <summary>在线人数 / 上限（由 ServerPingService 填充）。</summary>
    public int Online { get; set; }
    public int Max { get; set; }
    public string OnlineText => Max > 0 ? $"{Online}/{Max}人在线" : "状态未知";
}

/// <summary>主页「我的存档」条目。</summary>
public sealed class SaveInfo
{
    public string Version { get; set; } = "";   // 所属实例（版本）ID
    public string Name { get; set; } = "";      // 世界名（存档目录名）
    public long SizeBytes { get; set; }
    public DateTime Modified { get; set; }
    public string SizeText { get; set; } = "";
    public string FullPath { get; set; } = "";
}

/// <summary>日历中的一天（用于主页日历控件）。</summary>
public sealed class CalendarDay
{
    public int Day { get; init; }
    public bool InMonth { get; init; } = true;  // false = 前后补齐的空位
    public bool IsToday { get; init; }
    public bool HasPlayed { get; init; }        // 当天是否有游玩记录
    public string DayText => InMonth ? Day.ToString() : "";
}

/// <summary>「从最近的项目开始」条目。</summary>
public sealed class RecentProject
{
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";       // 版本标签，如 Fabric 26.2 / 26.3-Vanilla
    public string LastPlayedText { get; set; } = "";
    public bool IsServer { get; set; }
    public bool IsInstalled { get; set; } = true;   // 服务器恒为 true
    public object? Target { get; set; }         // VersionEntry 或 ServerEntry，用于启动/连接
}

/// <summary>主页「固定的实例」条目。</summary>
public sealed class PinnedInstance
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";          // 如 Fabric / Vanilla / 整合包
    public string LastPlayedText { get; set; } = ""; // 如「游玩于 上周」
    public VersionEntry? Entry { get; set; }
}
