namespace Wallet.Api.Security;

public sealed class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "wallet-bet-api";
    public string Audience { get; set; } = "wallet-bet-clients";
    public int ExpiryMinutes { get; set; } = 60;
}
