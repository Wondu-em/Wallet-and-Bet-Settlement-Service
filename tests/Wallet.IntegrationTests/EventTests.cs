using Microsoft.EntityFrameworkCore;
using Wallet.Application.Events;
using Wallet.Domain;
using Xunit;

namespace Wallet.IntegrationTests;

[Collection("PostgreSQL integration")]
public class EventTests(PostgresFixture fx)
{
    private Task<EventDto> CreateAsync(params OutcomeInput[] outcomes)
    {
        using var db = fx.CreateContext();
        return TestData.Events(db).CreateAsync(Guid.NewGuid(), "Validation event", outcomes);
    }

    [Fact]
    public async Task Create_event_makes_escrow_account_outcomes_and_history()
    {
        var ev = await TestData.CreateEventAsync(fx, 2.5m, 1.8m, 3.2m);

        Assert.Equal(EventStatus.Open, ev.Status);
        Assert.Equal(3, ev.Outcomes.Count);
        Assert.Contains(ev.Outcomes, o => o.OddsBp == 25_000);

        await using var db = fx.CreateContext();
        var escrowId = await TestData.GetEscrowIdAsync(fx, ev.Id);
        var escrow = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == escrowId);
        Assert.Equal(AccountType.EventEscrow, escrow.Type);

        var history = await db.EventHistory.AsNoTracking().Where(h => h.EventId == ev.Id).ToListAsync();
        Assert.Single(history);
        Assert.Equal(EventStatus.Open, history[0].ToStatus);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Event_needs_two_or_three_outcomes(int count)
    {
        var outcomes = Enumerable.Range(0, count).Select(i => new OutcomeInput($"O{i}", 2.0m)).ToArray();
        await Assert.ThrowsAsync<AppValidationException>(() => CreateAsync(outcomes));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(1000.01)]
    public async Task Odds_must_be_in_range(double odds)
    {
        await Assert.ThrowsAsync<AppValidationException>(() =>
            CreateAsync(new OutcomeInput("A", (decimal)odds), new OutcomeInput("B", 2.0m)));
    }

    [Fact]
    public async Task Duplicate_outcome_names_are_rejected()
        => await Assert.ThrowsAsync<AppValidationException>(() =>
            CreateAsync(new OutcomeInput("Home", 2.0m), new OutcomeInput("home", 3.0m)));

    [Fact]
    public async Task Closing_twice_is_harmless_and_records_history_once()
    {
        var ev = await TestData.CreateEventAsync(fx);
        var admin = Guid.NewGuid();

        await using (var db = fx.CreateContext())
        {
            var first = await TestData.Events(db).CloseAsync(admin, ev.Id);
            Assert.Equal(EventStatus.Closed, first.Status);
        }
        await using (var db = fx.CreateContext())
        {
            var second = await TestData.Events(db).CloseAsync(admin, ev.Id);
            Assert.Equal(EventStatus.Closed, second.Status);
        }

        await using var check = fx.CreateContext();
        var transitions = await check.EventHistory.CountAsync(h => h.EventId == ev.Id);
        Assert.Equal(2, transitions);   // created + closed, no duplicate "closed"
    }

    [Fact]
    public async Task Closing_an_unknown_event_is_not_found()
    {
        await using var db = fx.CreateContext();
        await Assert.ThrowsAsync<NotFoundException>(() => TestData.Events(db).CloseAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
