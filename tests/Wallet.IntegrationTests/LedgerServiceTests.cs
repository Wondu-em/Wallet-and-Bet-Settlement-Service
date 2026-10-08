using Wallet.Application.Ledger;
using Wallet.Domain;
using Wallet.Infrastructure.Ledger;
using Xunit;
using Microsoft.EntityFrameworkCore;

namespace Wallet.IntegrationTests;

public class LedgerServiceTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Deposit_increases_balance_and_matches_ledger()
    {
        var wallet = await fx.CreateWalletAsync();

        await using (var db = fx.CreateContext())
        {
            var runner = new TransactionRunner(db);
            var ledger = new LedgerService(db);
            await runner.RunAsync(ct => ledger.PostAsync(Postings.Deposit(wallet, 5_000), ct));
        }

        var (cached, derived) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(5_000, cached);
        Assert.Equal(5_000, derived);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Withdraw_more_than_balance_fails_and_changes_nothing()
    {
        var wallet = await fx.CreateWalletAsync(openingBalance: 1_000);

        await using (var db = fx.CreateContext())
        {
            var runner = new TransactionRunner(db);
            var ledger = new LedgerService(db);
            await Assert.ThrowsAsync<InsufficientFundsException>(() =>
                runner.RunAsync(ct => ledger.PostAsync(Postings.Withdraw(wallet, 2_000), ct)));
        }

        var (cached, derived) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(1_000, cached);
        Assert.Equal(1_000, derived);
    }

    [Fact]
    public async Task Unbalanced_posting_is_rejected()
    {
        var wallet = await fx.CreateWalletAsync(openingBalance: 1_000);
        var escrow = await fx.CreateEscrowAsync();

        var bad = new PostTransactionRequest(LedgerTransactionType.BetStake, "bet", Guid.NewGuid(), new PostingLine[]
        {
            new(wallet, EntryDirection.Debit, 100),
            new(escrow, EntryDirection.Credit, 90)
        });

        await using var db = fx.CreateContext();
        var runner = new TransactionRunner(db);
        var ledger = new LedgerService(db);
        await Assert.ThrowsAsync<LedgerException>(() => runner.RunAsync(ct => ledger.PostAsync(bad, ct)));
    }

    [Fact]
    public async Task Posting_outside_a_transaction_is_refused()
    {
        var wallet = await fx.CreateWalletAsync();
        await using var db = fx.CreateContext();
        var ledger = new LedgerService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.PostAsync(Postings.Deposit(wallet, 100)));
    }

    [Fact]
    public async Task Concurrent_stakes_cannot_overdraw_a_wallet()
    {
        const long openingBalance = 10_000;
        const long stake = 6_000;
        const int parallelBets = 20;

        var wallet = await fx.CreateWalletAsync(openingBalance);
        var escrow = await fx.CreateEscrowAsync();

        var tasks = Enumerable.Range(0, parallelBets).Select(async _ =>
        {
            await using var db = fx.CreateContext();     // one context per "request"
            var runner = new TransactionRunner(db);
            var ledger = new LedgerService(db);
            try
            {
                await runner.RunAsync(ct =>
                    ledger.PostAsync(Postings.BetStake(wallet, escrow, stake, Guid.NewGuid()), ct));
                return true;
            }
            catch (InsufficientFundsException)
            {
                return false;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));                 // exactly one bet fits
        var (cached, derived) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(openingBalance - stake, cached);           // 4_000, never negative
        Assert.Equal(openingBalance - stake, derived);
        var (escrowCached, escrowDerived) = await fx.GetBalancesAsync(escrow);
        Assert.Equal(stake, escrowCached);
        Assert.Equal(stake, escrowDerived);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Ledger_entries_are_append_only()
    {
        await fx.CreateWalletAsync(openingBalance: 100);   // guarantees at least one entry exists

        await using var db = fx.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE ledger_entries SET amount = amount"));
        Assert.Contains("append-only", ex.ToString());
    }
}
