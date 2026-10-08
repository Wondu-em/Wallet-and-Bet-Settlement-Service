using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Application.Ledger;
using Wallet.Domain;
using Wallet.Infrastructure.Ledger;
using Wallet.Infrastructure.Persistence;

namespace Wallet.IntegrationTests;

/// <summary>
/// Creates a throw-away database on the local PostgreSQL, applies all migrations (including the
/// ledger triggers), seeds system accounts, and drops the database afterwards.
/// Override the admin connection with env var TEST_PG_ADMIN if needed.
/// </summary>
public sealed class PostgresFixture : IDisposable
{
    private readonly string _adminCs;
    public string DbName { get; }
    public string ConnectionString { get; }

    public PostgresFixture()
    {
        _adminCs = Environment.GetEnvironmentVariable("TEST_PG_ADMIN")
                   ?? "Host=localhost;Port=5432;Database=postgres;Username=wallet;Password=wallet_dev_pw";
        DbName = $"walletbet_test_{Guid.NewGuid():N}";

        using (var conn = new NpgsqlConnection(_adminCs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{DbName}\"";
            cmd.ExecuteNonQuery();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminCs) { Database = DbName }.ConnectionString;

        using var db = CreateContext();
        db.Database.Migrate();
        DbSeeder.SeedSystemAccountsAsync(db).GetAwaiter().GetResult();
    }

    public WalletDbContext CreateContext()
        => new(new DbContextOptionsBuilder<WalletDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<Guid> CreateWalletAsync(long openingBalance = 0)
    {
        await using var db = CreateContext();
        var user = new User { Email = $"{Guid.NewGuid():N}@test.local", PasswordHash = "x" };
        var wallet = new Account { Type = AccountType.UserWallet, OwnerUserId = user.Id };
        db.Users.Add(user);
        db.Accounts.Add(wallet);
        await db.SaveChangesAsync();

        if (openingBalance > 0)
        {
            var runner = new TransactionRunner(db);
            var ledger = new LedgerService(db);
            await runner.RunAsync(ct => ledger.PostAsync(Postings.Deposit(wallet.Id, openingBalance, "test-seed"), ct));
        }
        return wallet.Id;
    }

    public async Task<Guid> CreateEscrowAsync()
    {
        await using var db = CreateContext();
        var escrow = new Account { Type = AccountType.EventEscrow };
        db.Accounts.Add(escrow);
        await db.SaveChangesAsync();
        return escrow.Id;
    }

    public async Task<(long Cached, long Derived)> GetBalancesAsync(Guid accountId)
    {
        await using var db = CreateContext();
        var cached = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.Balance).SingleAsync();
        var derived = await new LedgerService(db).GetLedgerBalanceAsync(accountId);
        return (cached, derived);
    }

    public async Task AssertLedgerGloballyBalancedAsync()
    {
        await using var db = CreateContext();
        var debits = await db.LedgerEntries.Where(e => e.Direction == EntryDirection.Debit).SumAsync(e => (long)e.Amount);
        var credits = await db.LedgerEntries.Where(e => e.Direction == EntryDirection.Credit).SumAsync(e => (long)e.Amount);
        Xunit.Assert.Equal(debits, credits);
    }

    public void Dispose()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            using var conn = new NpgsqlConnection(_adminCs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{DbName}\" WITH (FORCE)";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
