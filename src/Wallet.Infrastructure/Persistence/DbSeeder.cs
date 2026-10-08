using Microsoft.EntityFrameworkCore;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence;

public static class DbSeeder
{
    public static async Task SeedSystemAccountsAsync(WalletDbContext db, CancellationToken ct = default)
    {
        var system = new[]
        {
            (SystemAccounts.HouseId, AccountType.House),
            (SystemAccounts.ExternalClearingId, AccountType.ExternalClearing)
        };

        foreach (var (id, type) in system)
        {
            if (!await db.Accounts.AnyAsync(a => a.Id == id, ct))
                db.Accounts.Add(new Account { Id = id, Type = type });
        }
        await db.SaveChangesAsync(ct);
    }
}
