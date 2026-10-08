namespace Wallet.Domain;

public static class MoneyLimits
{
    /// <summary>Largest single deposit/withdrawal: 1,000,000.00 ETB expressed in minor units.</summary>
    public const long MaxSingleAmount = 100_000_000;
}
