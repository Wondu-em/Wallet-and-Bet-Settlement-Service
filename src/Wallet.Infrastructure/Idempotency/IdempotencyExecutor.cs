using System.Data;
using Microsoft.EntityFrameworkCore;
using Wallet.Application.Idempotency;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Idempotency;

/// <summary>
/// The idempotency record lives in the SAME database transaction as the business effect.
/// Concurrent duplicates block on the unique index until the first transaction finishes:
///   - first one commits  -> the duplicate's INSERT conflicts (0 rows) and replays the stored response;
///   - first one rolls back -> the duplicate's INSERT succeeds and it executes normally.
/// </summary>
public sealed class IdempotencyExecutor(WalletDbContext db) : IIdempotencyExecutor
{
    public async Task<IdempotentResponse> ExecuteAsync(
        IdempotencyRequest request,
        Func<CancellationToken, Task<IdempotentResponse>> work,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("IdempotencyExecutor must own the outermost transaction.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_keys (id, scope, endpoint, "key", request_hash, status, created_at)
            VALUES ({Guid.NewGuid()}, {request.Scope}, {request.Endpoint}, {request.Key}, {request.RequestHash}, 'InProgress', {DateTimeOffset.UtcNow})
            ON CONFLICT (scope, endpoint, "key") DO NOTHING
            """, ct);

        if (inserted == 0)
        {
            // Duplicate: the first request has committed (we waited for it).
            var existing = await db.IdempotencyKeys.AsNoTracking()
                .SingleAsync(k => k.Scope == request.Scope && k.Endpoint == request.Endpoint && k.Key == request.Key, ct);

            if (existing.RequestHash != request.RequestHash)
                throw new IdempotencyConflictException();

            return new IdempotentResponse(existing.ResponseCode ?? 500, existing.ResponseBody ?? "", Replayed: true);
        }

        // First request: run the business logic inside this transaction.
        var response = await work(ct);   // an exception here disposes tx => full rollback, key released

        if (response.StatusCode is < 200 or >= 300)
        {
            await tx.RollbackAsync(ct);  // do not cache failures
            return response;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE idempotency_keys
               SET status = 'Completed', response_code = {response.StatusCode}, response_body = {response.Body ?? ""}
             WHERE scope = {request.Scope} AND endpoint = {request.Endpoint} AND "key" = {request.Key}
            """, ct);

        await tx.CommitAsync(ct);
        return response;
    }
}
