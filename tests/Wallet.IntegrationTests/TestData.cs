using Wallet.Application.Events;
using Wallet.Application.Ledger;
using Wallet.Domain;
using Wallet.Infrastructure.Audit;
using Wallet.Infrastructure.Ledger;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Services;

namespace Wallet.IntegrationTests;

/// <summary>Helpers for building services per DbContext (one context = one "request").</summary>
public static class TestData
{
    public static BettingService Betting(WalletDbContext db)
        => new(db, new LedgerService(db), new TransactionRunner(db), new AuditWriter(db));

    public static EventService Events(WalletDbContext db)
        => new(db, new TransactionRunner(db), new AuditWriter(db));

    public static async Task<(Guid UserId, Guid WalletId)> CreateUserAsync(PostgresFixture fx, long balance = 0)
    {
        await using var db = fx.CreateContext();
        var user = new User { Email = $"{Guid.NewGuid():N}@test.local", PasswordHash = "x" };
        var wallet = new Account { Type = AccountType.UserWallet, OwnerUserId = user.Id };
        db.Users.Add(user);
        db.Accounts.Add(wallet);
        await db.SaveChangesAsync();

        if (balance > 0)
        {
            var runner = new TransactionRunner(db);
            var ledger = new LedgerService(db);
            await runner.RunAsync(ct => ledger.PostAsync(Postings.Deposit(wallet.Id, balance, "test-seed"), ct));
        }
        return (user.Id, wallet.Id);
    }

    public static async Task<EventDto> CreateEventAsync(PostgresFixture fx, params decimal[] odds)
    {
        if (odds.Length == 0) odds = new[] { 2.0m, 3.0m };
        var names = new[] { "Home", "Away", "Draw" };

        await using var db = fx.CreateContext();
        var outcomes = odds.Select((o, i) => new OutcomeInput(names[i], o)).ToList();
        return await Events(db).CreateAsync(Guid.NewGuid(), $"Test event {Guid.NewGuid():N}", outcomes);
    }

    public static async Task<Guid> GetEscrowIdAsync(PostgresFixture fx, Guid eventId)
    {
        await using var db = fx.CreateContext();
        return db.Events.Where(e => e.Id == eventId).Select(e => e.EscrowAccountId).Single();
    }
}
