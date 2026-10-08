namespace Wallet.Domain;

/// <summary>
/// Odds are stored as integer basis points: 2.50 => 25_000 (scale 10_000).
/// Payout = floor(stake * oddsBp / Scale), rounding down in the house's favour.
/// </summary>
public static class Odds
{
    public const int Scale = 10_000;

    public static long Payout(long stake, int oddsBp)
    {
        if (stake <= 0) throw new ArgumentOutOfRangeException(nameof(stake), "Stake must be positive.");
        if (oddsBp <= Scale) throw new ArgumentOutOfRangeException(nameof(oddsBp), "Odds must be greater than 1.00.");

        Int128 result = (Int128)stake * oddsBp / Scale;
        if (result > long.MaxValue) throw new OverflowException("Payout exceeds the maximum representable amount.");
        return (long)result;
    }

    /// <summary>Converts decimal odds (e.g. 2.50) to basis points, truncating extra precision.</summary>
    public static int FromDecimal(decimal odds)
        => checked((int)Math.Round(odds * Scale, 0, MidpointRounding.ToZero));
}
