using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wallet.Domain;
using Wallet.Infrastructure.Outbox;

namespace Wallet.IntegrationTests;

[Collection("PostgreSQL integration")]
public class OutboxTests(PostgresFixture fx)
{
    [Fact]
    public async Task Publisher_marks_persisted_outbox_message_as_processed_once()
    {
        var messageId = Guid.NewGuid();
        await using (var db = fx.CreateContext())
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = messageId,
                Type = "test.message",
                Payload = """{"test":true}""",
                CreatedAt = DateTimeOffset.UtcNow.AddYears(-10)
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateContext())
        {
            var processor = new OutboxProcessor(db, NullLogger<OutboxProcessor>.Instance);
            Assert.InRange(await processor.ProcessBatchAsync(), 1, 50);
            await processor.ProcessBatchAsync();
        }

        await using var check = fx.CreateContext();
        var message = await check.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == messageId);
        Assert.NotNull(message.ProcessedAt);
        Assert.Equal(1, message.Attempts);
    }

    [Fact]
    public async Task Event_creation_writes_outbox_message_with_event()
    {
        var ev = await TestData.CreateEventAsync(fx);

        await using var db = fx.CreateContext();
        var message = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == "event.created")
            .OrderByDescending(m => m.CreatedAt)
            .FirstAsync();

        Assert.Contains(ev.Id.ToString(), message.Payload);
        Assert.Null(message.ProcessedAt);
    }
}
