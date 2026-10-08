using System.Data;
using Microsoft.EntityFrameworkCore;
using Wallet.Application.Ledger;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Ledger;

public sealed class TransactionRunner(WalletDbContext db) : ITransactionRunner
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        // Already inside a transaction: join it, the outer scope owns commit/rollback.
        if (db.Database.CurrentTransaction is not null)
            return await work(ct);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var result = await work(ct);
        await tx.CommitAsync(ct);   // disposing without commit => rollback
        return result;
    }
}
