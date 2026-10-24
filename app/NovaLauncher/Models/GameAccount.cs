namespace NovaLauncher.Models;

public enum AccountType { Offline, Yggdrasil, Microsoft }

public sealed class GameAccount
{
    public AccountType Type { get; set; } = AccountType.Offline;
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public string AuthServer { get; set; } = ""; // Yggdrasil 服务器地址
    public bool Selected { get; set; }

    public string TypeLabel => Type switch
    {
        AccountType.Offline => "离线",
        AccountType.Yggdrasil => "Yggdrasil",
        AccountType.Microsoft => "微软",
        _ => "?"
    };

    public string UuidShort => Uuid.Length >= 8 ? Uuid[..8] : Uuid;

    public string UserType => Type switch
    {
        AccountType.Microsoft => "msa",
        AccountType.Yggdrasil => "mojang",
        _ => "legacy",
    };
}
