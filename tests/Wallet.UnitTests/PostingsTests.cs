using Wallet.Application.Ledger;
using Wallet.Domain;
using Xunit;

namespace Wallet.UnitTests;

public class PostingsTests
{
    private static readonly Guid Wallet = Guid.NewGuid();
    private static readonly Guid Escrow = Guid.NewGuid();
    private static readonly Guid Bet = Guid.NewGuid();

    public static IEnumerable<object[]> AllPostings() => new[]
    {
        new object[] { Postings.Deposit(Wallet, 500) },
        new object[] { Postings.Withdraw(Wallet, 500) },
        new object[] { Postings.BetStake(Wallet, Escrow, 500, Bet) },
        new object[] { Postings.Payout(Escrow, Wallet, 500, Bet) },
        new object[] { Postings.Refund(Escrow, Wallet, 500, Bet) },
    };

    [Theory]
    [MemberData(nameof(AllPostings))]
    public void Every_posting_rule_is_balanced(PostTransactionRequest r)
    {
        var debits = r.Lines.Where(l => l.Direction == EntryDirection.Debit).Sum(l => l.Amount);
        var credits = r.Lines.Where(l => l.Direction == EntryDirection.Credit).Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(500, debits);
    }

    [Fact]
    public void Deposit_debits_clearing_and_credits_wallet()
    {
        var r = Postings.Deposit(Wallet, 100);
        Assert.Contains(r.Lines, l => l.AccountId == SystemAccounts.ExternalClearingId && l.Direction == EntryDirection.Debit);
        Assert.Contains(r.Lines, l => l.AccountId == Wallet && l.Direction == EntryDirection.Credit);
    }
}
