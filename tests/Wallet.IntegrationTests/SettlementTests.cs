using Microsoft.EntityFrameworkCore;
using Wallet.Application.Events;
using Wallet.Application.Idempotency;
using Wallet.Application.Ledger;
using Wallet.Application.Settlement;
using Wallet.Domain;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Ledger;
using Xunit;
using static Wallet.IntegrationTests.SettlementHelpers;

namespace Wallet.IntegrationTests;

[Collection("PostgreSQL integration")]
public class SettlementTests(PostgresFixture fx)
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Scenario(
        EventDto Event, OutcomeDto Home, OutcomeDto Away,
        (Guid UserId, Guid WalletId) A, (Guid UserId, Guid WalletId) B, (Guid UserId, Guid WalletId) C,
        Guid EscrowId);

    /// <summary>Home pays 2.0, Away pays 3.0. A bets 1000 and B bets 2000 on Home; C bets 4000 on Away. Each starts with 5000.</summary>
    private async Task<Scenario> SetupAsync(bool close = true)
    {
        var ev = await TestData.CreateEventAsync(fx, 2.0m, 3.0m);
        var home = ev.Outcomes.Single(o => o.Name == "Home");
        var away = ev.Outcomes.Single(o => o.Name == "Away");

        var a = await TestData.CreateUserAsync(fx, 5_000);
        var b = await TestData.CreateUserAsync(fx, 5_000);
        var c = await TestData.CreateUserAsync(fx, 5_000);

        await BetAsync(a.UserId, home.Id, 1_000);
        await BetAsync(b.UserId, home.Id, 2_000);
        await BetAsync(c.UserId, away.Id, 4_000);

        if (close)
        {
            await using var db = fx.CreateContext();
            await TestData.Events(db).CloseAsync(Admin, ev.Id);
        }

        return new Scenario(ev, home, away, a, b, c, await TestData.GetEscrowIdAsync(fx, ev.Id));
    }

    private async Task BetAsync(Guid userId, Guid outcomeId, long stake)
    {
        await using var db = fx.CreateContext();
        await TestData.Betting(db).PlaceBetAsync(userId, outcomeId, stake);
    }

    private async Task<long> CachedAsync(Guid accountId) => (await fx.GetBalancesAsync(accountId)).Cached;

    private async Task AssertBalanceAsync(Guid accountId, long expected)
    {
        var (cached, derived) = await fx.GetBalancesAsync(accountId);
        Assert.Equal(expected, cached);
        Assert.Equal(expected, derived);
    }

    private async Task<int> CountPayoutTransactionsAsync(Guid eventId)
    {
        await using var db = fx.CreateContext();
        var betIds = await db.Bets.Where(b => b.EventId == eventId).Select(b => b.Id).ToListAsync();
        return await db.LedgerTransactions.CountAsync(t =>
            t.Type == LedgerTransactionType.Payout && t.ReferenceId != null && betIds.Contains(t.ReferenceId.Value));
    }

    // ------------------------------------------------------------------ settle

    [Fact]
    public async Task Settle_pays_winners_marks_losers_and_empties_escrow()
    {
        var s = await SetupAsync();
        var houseBefore = await CachedAsync(SystemAccounts.HouseId);

        SettlementResultDto result;
        await using (var db = fx.CreateContext())
            result = await Settlement(db).SettleAsync(Admin, s.Event.Id, s.Home.Id);

        Assert.Equal(EventStatus.Settled, result.Status);
        Assert.Equal(2, result.WinningBets);
        Assert.Equal(1, result.LosingBets);
        Assert.Equal(7_000, result.TotalStaked);
        Assert.Equal(6_000, result.TotalPaidOut);      // 1000*2.0 + 2000*2.0
        Assert.Equal(1_000, result.HouseResult);

        await AssertBalanceAsync(s.A.WalletId, 6_000);  // 5000 - 1000 + 2000
        await AssertBalanceAsync(s.B.WalletId, 7_000);  // 5000 - 2000 + 4000
        await AssertBalanceAsync(s.C.WalletId, 1_000);  // lost 4000
        await AssertBalanceAsync(s.EscrowId, 0);
        Assert.Equal(houseBefore + 1_000, await CachedAsync(SystemAccounts.HouseId));

        await using var check = fx.CreateContext();
        var bets = await check.Bets.AsNoTracking().Where(b => b.EventId == s.Event.Id).ToListAsync();
        Assert.Equal(BetStatus.Won, bets.Single(b => b.UserId == s.A.UserId).Status);
        Assert.Equal(2_000, bets.Single(b => b.UserId == s.A.UserId).Payout);
        Assert.Equal(4_000, bets.Single(b => b.UserId == s.B.UserId).Payout);
        Assert.Equal(BetStatus.Lost, bets.Single(b => b.UserId == s.C.UserId).Status);

        // status history: every bet went Placed -> final, the event went Closed -> Settled
        var history = await check.BetHistory.AsNoTracking()
            .Where(h => bets.Select(b => b.Id).Contains(h.BetId) && h.FromStatus == BetStatus.Placed).ToListAsync();
        Assert.Equal(3, history.Count);
        Assert.All(history, h => Assert.Equal(Admin, h.ActorId));
        Assert.True(await check.EventHistory.AnyAsync(h =>
            h.EventId == s.Event.Id && h.FromStatus == EventStatus.Closed && h.ToStatus == EventStatus.Settled));
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == "event.settle" && a.EntityId == s.Event.Id.ToString()));

        await LedgerAssertions.AssertCachedBalancesMatchLedgerAsync(fx);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Settling_twice_fails_and_pays_only_once()
    {
        var s = await SetupAsync();

        await using (var db = fx.CreateContext())
            await Settlement(db).SettleAsync(Admin, s.Event.Id, s.Home.Id);

        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<ConflictException>(() => Settlement(db).SettleAsync(Admin, s.Event.Id, s.Home.Id));

        // even a different winner is refused
        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<ConflictException>(() => Settlement(db).SettleAsync(Admin, s.Event.Id, s.Away.Id));

        Assert.Equal(2, await CountPayoutTransactionsAsync(s.Event.Id));
        await AssertBalanceAsync(s.A.WalletId, 6_000);
        await AssertBalanceAsync(s.C.WalletId, 1_000);
    }

    [Fact]
    public async Task Concurrent_settlements_apply_exactly_once()
    {
        var s = await SetupAsync();

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            try
            {
                await Settlement(db).SettleAsync(Admin, s.Event.Id, s.Home.Id);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(2, await CountPayoutTransactionsAsync(s.Event.Id));
        await AssertBalanceAsync(s.A.WalletId, 6_000);
        await AssertBalanceAsync(s.B.WalletId, 7_000);
        await AssertBalanceAsync(s.EscrowId, 0);
        await LedgerAssertions.AssertCachedBalancesMatchLedgerAsync(fx);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Same_idempotency_key_settles_once_and_replays_the_response()
    {
        var s = await SetupAsync();
        var key = Guid.NewGuid().ToString();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            var executor = new IdempotencyExecutor(db);
            var settlement = Settlement(db);
            return await executor.ExecuteAsync(
                new IdempotencyRequest("admin:test", $"POST /admin/events/{s.Event.Id}/settle", key, "same-payload"),
                async ct => IdempotentResponse.Of(200, await settlement.SettleAsync(Admin, s.Event.Id, s.Home.Id, ct)));
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(9, results.Count(r => r.Replayed));
        Assert.Single(results.Select(r => r.Body).Distinct());
        Assert.All(results, r => Assert.Equal(200, r.StatusCode));
        Assert.Equal(2, await CountPayoutTransactionsAsync(s.Event.Id));
        await AssertBalanceAsync(s.EscrowId, 0);
    }

    [Fact]
    public async Task A_failure_after_settlement_work_rolls_everything_back()
    {
        var s = await SetupAsync();

        await using (var db = fx.CreateContext())
        {
            var runner = new TransactionRunner(db);
            var settlement = Settlement(db);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync<int>(async ct =>
            {
                await settlement.SettleAsync(Admin, s.Event.Id, s.Home.Id, ct);   // does all the work...
                throw new InvalidOperationException("crash before commit");        // ...then the transaction dies
            }));
        }

        await using var check = fx.CreateContext();
        Assert.Equal(EventStatus.Closed, (await check.Events.AsNoTracking().SingleAsync(e => e.Id == s.Event.Id)).Status);
        Assert.Equal(3, await check.Bets.CountAsync(b => b.EventId == s.Event.Id && b.Status == BetStatus.Placed));
        Assert.Equal(0, await CountPayoutTransactionsAsync(s.Event.Id));

        await AssertBalanceAsync(s.A.WalletId, 4_000);   // stakes still taken, nothing paid
        await AssertBalanceAsync(s.B.WalletId, 3_000);
        await AssertBalanceAsync(s.C.WalletId, 1_000);
        await AssertBalanceAsync(s.EscrowId, 7_000);
        await LedgerAssertions.AssertCachedBalancesMatchLedgerAsync(fx);

        // and a later, clean settlement still works
        await using var retry = fx.CreateContext();
        var result = await Settlement(retry).SettleAsync(Admin, s.Event.Id, s.Home.Id);
        Assert.Equal(EventStatus.Settled, result.Status);
    }

    [Fact]
    public async Task Settling_an_open_event_is_refused()
    {
        var s = await SetupAsync(close: false);
        await using var db = fx.CreateContext();
        await Assert.ThrowsAsync<ConflictException>(() => Settlement(db).SettleAsync(Admin, s.Event.Id, s.Home.Id));
        await AssertBalanceAsync(s.EscrowId, 7_000);
    }

    [Fact]
    public async Task Winning_outcome_must_belong_to_the_event()
    {
        var s = await SetupAsync();
        var other = await TestData.CreateEventAsync(fx);

        await using var db = fx.CreateContext();
        await Assert.ThrowsAsync<AppValidationException>(() =>
            Settlement(db).SettleAsync(Admin, s.Event.Id, other.Outcomes[0].Id));
    }

    [Fact]
    public async Task Settling_an_event_with_no_bets_just_marks_it_settled()
    {
        var ev = await TestData.CreateEventAsync(fx);
        await using (var db = fx.CreateContext())
            await TestData.Events(db).CloseAsync(Admin, ev.Id);

        await using var settle = fx.CreateContext();
        var result = await Settlement(settle).SettleAsync(Admin, ev.Id, ev.Outcomes[0].Id);

        Assert.Equal(EventStatus.Settled, result.Status);
        Assert.Equal(0, result.TotalStaked);
        Assert.Equal(0, result.HouseResult);
    }

    // -------------------------------------------------------------------- void

    [Fact]
    public async Task Void_refunds_every_stake_and_marks_bets_refunded()
    {
        var s = await SetupAsync();
        var houseBefore = await CachedAsync(SystemAccounts.HouseId);

        VoidResultDto result;
        await using (var db = fx.CreateContext())
            result = await Settlement(db).VoidAsync(Admin, s.Event.Id, "match abandoned");

        Assert.Equal(EventStatus.Voided, result.Status);
        Assert.Equal(3, result.BetsRefunded);
        Assert.Equal(7_000, result.TotalRefunded);

        await AssertBalanceAsync(s.A.WalletId, 5_000);   // everyone is whole again
        await AssertBalanceAsync(s.B.WalletId, 5_000);
        await AssertBalanceAsync(s.C.WalletId, 5_000);
        await AssertBalanceAsync(s.EscrowId, 0);
        Assert.Equal(houseBefore, await CachedAsync(SystemAccounts.HouseId));   // house is untouched

        await using var check = fx.CreateContext();
        Assert.Equal(3, await check.Bets.CountAsync(b => b.EventId == s.Event.Id && b.Status == BetStatus.Refunded));
        Assert.True(await check.EventHistory.AnyAsync(h =>
            h.EventId == s.Event.Id && h.FromStatus == EventStatus.Closed && h.ToStatus == EventStatus.Voided
            && h.Reason == "match abandoned"));
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == "event.void" && a.EntityId == s.Event.Id.ToString()));

        await LedgerAssertions.AssertCachedBalancesMatchLedgerAsync(fx);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task An_open_event_can_be_voided_and_then_rejects_bets()
    {
        var s = await SetupAsync(close: false);

        await using (var db = fx.CreateContext())
            await Settlement(db).VoidAsync(Admin, s.Event.Id, null);

        await AssertBalanceAsync(s.A.WalletId, 5_000);

        await using var bet = fx.CreateContext();
        await Assert.ThrowsAsync<ConflictException>(() =>
            TestData.Betting(bet).PlaceBetAsync(s.A.UserId, s.Home.Id, 100));
    }

    [Fact]
    public async Task Settled_and_voided_events_cannot_change_state_again()
    {
        var settled = await SetupAsync();
        await using (var db = fx.CreateContext())
            await Settlement(db).SettleAsync(Admin, settled.Event.Id, settled.Home.Id);
        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<ConflictException>(() => Settlement(db).VoidAsync(Admin, settled.Event.Id, null));

        var voided = await SetupAsync();
        await using (var db = fx.CreateContext())
            await Settlement(db).VoidAsync(Admin, voided.Event.Id, null);
        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<ConflictException>(() => Settlement(db).VoidAsync(Admin, voided.Event.Id, null));
        await using (var db = fx.CreateContext())
            await Assert.ThrowsAsync<ConflictException>(() => Settlement(db).SettleAsync(Admin, voided.Event.Id, voided.Home.Id));

        await AssertBalanceAsync(settled.A.WalletId, 6_000);   // the refusals changed nothing
        await AssertBalanceAsync(voided.A.WalletId, 5_000);
    }
}
