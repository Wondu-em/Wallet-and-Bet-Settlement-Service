using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Outbox;

public sealed class OutboxProcessor(WalletDbContext db, ILogger<OutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var messages = await db.OutboxMessages
            .FromSqlRaw("""
                SELECT *
                  FROM outbox_messages
                 WHERE processed_at IS NULL
                 ORDER BY created_at, id
                 LIMIT 50
                 FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        var processedAt = DateTimeOffset.UtcNow;
        foreach (var message in messages)
        {
            logger.LogInformation(
                "Processed outbox message {OutboxMessageId} of type {OutboxMessageType}",
                message.Id, message.Type);
            message.Attempts = checked(message.Attempts + 1);
            message.ProcessedAt = processedAt;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return messages.Count;
    }
}
