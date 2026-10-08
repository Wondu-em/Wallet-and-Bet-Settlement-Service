using Wallet.Domain;
using Xunit;

namespace Wallet.UnitTests;

public class OddsTests
{
    [Theory]
    [InlineData(10_000, 25_000, 25_000)]   // 100.00 at 2.50 -> 250.00
    [InlineData(333, 15_000, 499)]         // 499.5 rounds down
    [InlineData(1, 10_001, 1)]             // 1.0001 rounds down
    [InlineData(5_000, 20_000, 10_000)]
    public void Payout_rounds_down_in_house_favour(long stake, int oddsBp, long expected)
        => Assert.Equal(expected, Odds.Payout(stake, oddsBp));

    [Theory]
    [InlineData(0, 20_000)]
    [InlineData(-5, 20_000)]
    [InlineData(100, 10_000)]
    [InlineData(100, 5_000)]
    public void Payout_rejects_invalid_input(long stake, int oddsBp)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Odds.Payout(stake, oddsBp));

    [Fact]
    public void Payout_throws_on_overflow()
        => Assert.Throws<OverflowException>(() => Odds.Payout(long.MaxValue, 20_000));

    [Fact]
    public void FromDecimal_converts_to_basis_points()
        => Assert.Equal(25_000, Odds.FromDecimal(2.50m));
}
