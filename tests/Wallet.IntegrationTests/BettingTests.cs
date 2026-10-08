using Microsoft.EntityFrameworkCore;
using Wallet.Application.Idempotency;
using Wallet.Domain;
using Wallet.Infrastructure.Idempotency;
using Xunit;

namespace Wallet.IntegrationTests;

public class BettingTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Placing_a_bet_debits_wallet_credits_escrow_and_records_everything()
    {
        var ev = await TestData.CreateEventAsync(fx, 2.5m, 1.8m);
        var (userId, walletId) = await TestData.CreateUserAsync(fx, balance: 10_000);

        await using (var db = fx.CreateContext())
        {
            var placed = await TestData.Betting(db).PlaceBetAsync(userId, ev.Outcomes[0].Id, 4_000);

            Assert.Equal(BetStatus.Placed, placed.Bet.Status);
            Assert.Equal(ev.Outcomes[0].OddsBp, placed.Bet.OddsBp);             // odds snapshot
            Assert.Equal(4_000 * placed.Bet.OddsBp / Odds.Scale, placed.Bet.PotentialPayout);
            Assert.Equal(6_000, placed.Balance);
        }

        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(6_000, cached);
        Assert.Equal(6_000, derived);

        var escrowId = await TestData.GetEscrowIdAsync(fx, ev.Id);
        var (escrowCached, escrowDerived) = await fx.GetBalancesAsync(escrowId);
        Assert.Equal(4_000, escrowCached);
        Assert.Equal(4_000, escrowDerived);

        await using var check = fx.CreateContext();
        Assert.Equal(1, await check.Bets.CountAsync(b => b.UserId == userId));
        Assert.Equal(1, await check.BetHistory.CountAsync(h => h.ToStatus == BetStatus.Placed && h.ActorId == userId));
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == "bet.place" && a.ActorId == userId));
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Bet_on_a_closed_event_is_rejected_and_costs_nothing()
    {
        var ev = await TestData.CreateEventAsync(fx);
        var (userId, walletId) = await TestData.CreateUserAsync(fx, balance: 5_000);

        await using (var db = fx.CreateContext())
            await TestData.Events(db).CloseAsync(Guid.NewGuid(), ev.Id);

        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<ConflictException>(() =>
                TestData.Betting(db).PlaceBetAsync(userId, ev.Outcomes[0].Id, 1_000));

        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(5_000, cached);
        Assert.Equal(5_000, derived);
    }

    [Fact]
    public async Task Insufficient_funds_rejects_the_bet_and_creates_no_bet_row()
    {
        var ev = await TestData.CreateEventAsync(fx);
        var (userId, walletId) = await TestData.CreateUserAsync(fx, balance: 1_000);

        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<InsufficientFundsException>(() =>
                TestData.Betting(db).PlaceBetAsync(userId, ev.Outcomes[0].Id, 2_000));

        await using var check = fx.CreateContext();
        Assert.Equal(0, await check.Bets.CountAsync(b => b.UserId == userId));
        Assert.Equal(0, await check.BetHistory.CountAsync(h => h.ActorId == userId));
        var (cached, _) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(1_000, cached);
    }

    [Fact]
    public async Task Unknown_outcome_is_not_found()
    {
        var (userId, _) = await TestData.CreateUserAsync(fx, balance: 1_000);
        await using var db = fx.CreateContext();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            TestData.Betting(db).PlaceBetAsync(userId, Guid.NewGuid(), 100));
    }

    [Fact]
    public async Task Concurrent_bets_cannot_overdraw_the_wallet()
    {
        const long balance = 10_000;
        const long stake = 6_000;

        var ev = await TestData.CreateEventAsync(fx);
        var (userId, walletId) = await TestData.CreateUserAsync(fx, balance);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            try
            {
                await TestData.Betting(db).PlaceBetAsync(userId, ev.Outcomes[0].Id, stake);
                return true;
            }
            catch (InsufficientFundsException)
            {
                return false;
            }
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
        await using var check = fx.CreateContext();
        Assert.Equal(1, await check.Bets.CountAsync(b => b.UserId == userId));

        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(balance - stake, cached);
        Assert.Equal(balance - stake, derived);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Duplicate_bet_requests_with_the_same_key_place_exactly_one_bet()
    {
        var ev = await TestData.CreateEventAsync(fx);
        var (userId, walletId) = await TestData.CreateUserAsync(fx, balance: 10_000);
        var key = Guid.NewGuid().ToString();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            var executor = new IdempotencyExecutor(db);
            var betting = TestData.Betting(db);

            return await executor.ExecuteAsync(
                new IdempotencyRequest($"user:{userId}", "POST /bets", key, "same-payload"),
                async ct =>
                {
                    var placed = await betting.PlaceBetAsync(userId, ev.Outcomes[0].Id, 1_000, ct);
                    return IdempotentResponse.Of(201, placed);
                });
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(9, results.Count(r => r.Replayed));
        Assert.Single(results.Select(r => r.Body).Distinct());           // every caller got the same response

        await using var check = fx.CreateContext();
        Assert.Equal(1, await check.Bets.CountAsync(b => b.UserId == userId));
        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(9_000, cached);                                      // charged once
        Assert.Equal(9_000, derived);
    }

    [Fact]
    public async Task Closing_the_event_while_bets_are_in_flight_never_lets_a_bet_in_after_close()
    {
        var ev = await TestData.CreateEventAsync(fx);
        var users = new List<Guid>();
        for (var i = 0; i < 10; i++) users.Add((await TestData.CreateUserAsync(fx, balance: 1_000)).UserId);

        var betTasks = users.Select(u => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            try { await TestData.Betting(db).PlaceBetAsync(u, ev.Outcomes[0].Id, 100); return true; }
            catch (ConflictException) { return false; }
        })).ToList();

        var closeTask = Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            await TestData.Events(db).CloseAsync(Guid.NewGuid(), ev.Id);
        });

        await Task.WhenAll(betTasks.Cast<Task>().Append(closeTask));

        // Every bet that exists was placed while the event was still Open:
        // its placement time is before the close transition time.
        await using var check = fx.CreateContext();
        var closedAt = await check.EventHistory
            .Where(h => h.EventId == ev.Id && h.ToStatus == EventStatus.Closed)
            .Select(h => h.At).SingleAsync();
        var lateBets = await check.Bets.CountAsync(b => b.EventId == ev.Id && b.PlacedAt > closedAt);
        Assert.Equal(0, lateBets);
    }
}
