using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Audit;
using Wallet.Infrastructure.Ledger;
using Wallet.Infrastructure.Outbox;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Services;
using Xunit;

namespace Wallet.IntegrationTests;

public static class SettlementHelpers
{
    public static SettlementService Settlement(WalletDbContext db)
        => new(db, new LedgerService(db), new TransactionRunner(db), new AuditWriter(db), new OutboxWriter(db));
}

public static class LedgerAssertions
{
    /// <summary>Every account's cached balance must equal SUM(credits) - SUM(debits) from the ledger.</summary>
    public static async Task AssertCachedBalancesMatchLedgerAsync(PostgresFixture fx)
    {
        await using var db = fx.CreateContext();

        var net = await db.LedgerEntries.AsNoTracking()
            .GroupBy(e => e.AccountId)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(e => e.Direction == EntryDirection.Credit ? e.Amount : -e.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Net);

        foreach (var account in await db.Accounts.AsNoTracking().ToListAsync())
            Assert.Equal(net.GetValueOrDefault(account.Id), account.Balance);
    }
}
