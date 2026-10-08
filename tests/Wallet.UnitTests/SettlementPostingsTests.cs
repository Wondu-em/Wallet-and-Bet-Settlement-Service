using Wallet.Application.Ledger;
using Wallet.Domain;
using Xunit;

namespace Wallet.UnitTests;

public class SettlementPostingsTests
{
    private static readonly Guid Escrow = Guid.NewGuid();

    [Theory]
    [InlineData(500L)]
    [InlineData(-500L)]
    public void Sweep_is_balanced_in_both_directions(long residual)
    {
        var r = SettlementPostings.Sweep(Escrow, residual, Guid.NewGuid());
        var debits = r.Lines.Where(l => l.Direction == EntryDirection.Debit).Sum(l => l.Amount);
        var credits = r.Lines.Where(l => l.Direction == EntryDirection.Credit).Sum(l => l.Amount);
        Assert.Equal(500, debits);
        Assert.Equal(500, credits);
        Assert.Equal(LedgerTransactionType.Sweep, r.Type);
    }

    [Fact]
    public void Positive_residual_moves_money_from_escrow_to_house()
    {
        var r = SettlementPostings.Sweep(Escrow, 100, Guid.NewGuid());
        Assert.Contains(r.Lines, l => l.AccountId == Escrow && l.Direction == EntryDirection.Debit);
        Assert.Contains(r.Lines, l => l.AccountId == SystemAccounts.HouseId && l.Direction == EntryDirection.Credit);
    }

    [Fact]
    public void Negative_residual_moves_money_from_house_to_escrow()
    {
        var r = SettlementPostings.Sweep(Escrow, -100, Guid.NewGuid());
        Assert.Contains(r.Lines, l => l.AccountId == SystemAccounts.HouseId && l.Direction == EntryDirection.Debit);
        Assert.Contains(r.Lines, l => l.AccountId == Escrow && l.Direction == EntryDirection.Credit);
    }

    [Fact]
    public void Zero_residual_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SettlementPostings.Sweep(Escrow, 0, Guid.NewGuid()));
}
