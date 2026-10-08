using System.Diagnostics;
using System.Text.Json;
using Wallet.Application;
using Wallet.Application.Audit;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Audit;

public sealed class AuditWriter(WalletDbContext db) : IAuditWriter
{
    public void Add(Guid? actorId, string action, string entityType, string? entityId, object? data = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Data = JsonSerializer.Serialize(data ?? new { }, AppJson.Options),
            CorrelationId = Activity.Current?.TraceId.ToString()
        });
    }
}
