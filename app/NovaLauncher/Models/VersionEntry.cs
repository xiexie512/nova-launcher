namespace NovaLauncher.Models;

public sealed class VersionEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release"; // release / snapshot / old_beta / old_alpha
    public string ReleaseTime { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Installed { get; set; }
    public long InstallSizeMb { get; set; }

    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "旧版 Beta",
        "old_alpha" => "旧版 Alpha",
        _ => Type,
    };

    public bool IsRelease => Type == "release";
    public bool IsSnapshot => Type == "snapshot";
    public bool IsOther => !IsRelease && !IsSnapshot;
}
