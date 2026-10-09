using System.Text.Json;
using Wallet.Application.Outbox;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Outbox;

public sealed class OutboxWriter(WalletDbContext db) : IOutboxWriter
{
    public void Add(string type, object payload)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 200)
            throw new ArgumentException("Outbox message type must contain 1 to 200 characters.", nameof(type));

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = type,
            Payload = JsonSerializer.Serialize(payload, Wallet.Application.AppJson.Options)
        });
    }
}
