using Microsoft.EntityFrameworkCore;
using Wallet.Application.Audit;
using Wallet.Application.Events;
using Wallet.Application.Ledger;
using Wallet.Application.Wallets;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Services;

public sealed class EventService(WalletDbContext db, ITransactionRunner runner, IAuditWriter audit) : IEventService
{
    public async Task<EventDto> CreateAsync(
        Guid adminId, string title, IReadOnlyList<OutcomeInput> outcomes, CancellationToken ct = default)
    {
        title = (title ?? "").Trim();
        Validate(title, outcomes);

        var escrow = new Account { Type = AccountType.EventEscrow };
        var ev = new BettingEvent { Title = title, EscrowAccountId = escrow.Id, Status = EventStatus.Open };
        foreach (var o in outcomes)
            ev.Outcomes.Add(new Outcome { EventId = ev.Id, Name = o.Name.Trim(), OddsBp = Odds.FromDecimal(o.Odds) });

        db.Accounts.Add(escrow);
        db.Events.Add(ev);
        db.EventHistory.Add(new EventStatusHistory
        {
            EventId = ev.Id, FromStatus = null, ToStatus = EventStatus.Open, ActorId = adminId, Reason = "event created"
        });
        audit.Add(adminId, "event.create", "event", ev.Id.ToString(),
            new { title, outcomes = ev.Outcomes.Select(o => new { o.Name, o.OddsBp }) });

        await db.SaveChangesAsync(ct);   // one implicit transaction: escrow + event + outcomes + history + audit
        return EventMapping.ToDto(ev);
    }

    public Task<EventDto> CloseAsync(Guid adminId, Guid eventId, CancellationToken ct = default)
        => runner.RunAsync(async token =>
        {
            // Exclusive lock: waits for in-flight bets (which hold FOR SHARE) and blocks new ones until commit.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM events WHERE id = {eventId} FOR UPDATE", token);

            var ev = await db.Events.Include(e => e.Outcomes).SingleOrDefaultAsync(e => e.Id == eventId, token)
                     ?? throw new NotFoundException("Event not found.");

            switch (ev.Status)
            {
                case EventStatus.Open:
                    ev.Status = EventStatus.Closed;
                    db.EventHistory.Add(new EventStatusHistory
                    {
                        EventId = ev.Id, FromStatus = EventStatus.Open, ToStatus = EventStatus.Closed,
                        ActorId = adminId, Reason = "closed for betting"
                    });
                    audit.Add(adminId, "event.close", "event", ev.Id.ToString());
                    await db.SaveChangesAsync(token);
                    break;

                case EventStatus.Closed:
                    break;   // already closed: repeatable, no new history

                default:
                    throw new ConflictException($"Event is {ev.Status} and cannot be closed.");
            }

            return EventMapping.ToDto(ev);
        }, ct);

    public async Task<EventDto> GetAsync(Guid eventId, CancellationToken ct = default)
    {
        var ev = await db.Events.AsNoTracking().Include(e => e.Outcomes)
                     .SingleOrDefaultAsync(e => e.Id == eventId, ct)
                 ?? throw new NotFoundException("Event not found.");
        return EventMapping.ToDto(ev);
    }

    public async Task<PagedResult<EventDto>> ListAsync(EventStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Events.AsNoTracking();
        if (status is not null) query = query.Where(e => e.Status == status);

        var total = await query.LongCountAsync(ct);
        var events = await query.Include(e => e.Outcomes)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<EventDto>(events.Select(EventMapping.ToDto).ToList(), page, pageSize, total);
    }

    private static void Validate(string title, IReadOnlyList<OutcomeInput> outcomes)
    {
        var errors = new Dictionary<string, string[]>();

        if (title.Length is 0 or > 200)
            errors["title"] = new[] { "Title is required (max 200 characters)." };

        if (outcomes is null || outcomes.Count is < 2 or > 3)
        {
            errors["outcomes"] = new[] { "An event needs 2 to 3 outcomes." };
        }
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < outcomes.Count; i++)
            {
                var o = outcomes[i];
                var name = (o.Name ?? "").Trim();
                if (name.Length is 0 or > 100)
                    errors[$"outcomes[{i}].name"] = new[] { "Name is required (max 100 characters)." };
                else if (!names.Add(name))
                    errors[$"outcomes[{i}].name"] = new[] { "Outcome names must be unique within an event." };

                if (o.Odds <= 1m || o.Odds > 1000m)
                    errors[$"outcomes[{i}].odds"] = new[] { "Odds must be greater than 1.00 and at most 1000.00." };
                else if (decimal.Round(o.Odds, 4) != o.Odds)
                    errors[$"outcomes[{i}].odds"] = new[] { "Odds may have at most 4 decimal places." };
            }
        }

        if (errors.Count > 0) throw new AppValidationException(errors);
    }
}

internal static class EventMapping
{
    public static EventDto ToDto(BettingEvent e) => new(
        e.Id, e.Title, e.Status, e.WinningOutcomeId, e.CreatedAt, e.SettledAt,
        e.Outcomes.OrderBy(o => o.Name)
            .Select(o => new OutcomeDto(o.Id, o.Name, o.OddsBp / (decimal)Odds.Scale, o.OddsBp))
            .ToList());
}
