using Microsoft.EntityFrameworkCore;
using Wallet.Application.Audit;
using Wallet.Application.Ledger;
using Wallet.Application.Outbox;
using Wallet.Application.Settlement;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Services;

/// <summary>
/// Lock order (prevents deadlocks between concurrent settlements, deposits and bets):
///   1. the event row, FOR UPDATE (waits for in-flight bets, blocks new ones)
///   2. every account involved (escrow, House, all bettor wallets) in ONE statement, ordered by id
/// Everything runs in one database transaction owned by the caller (see ITransactionRunner).
/// </summary>
public sealed class SettlementService(
    WalletDbContext db,
    ILedgerService ledger,
    ITransactionRunner runner,
    IAuditWriter audit,
    IOutboxWriter outbox) : ISettlementService
{
    public Task<SettlementResultDto> SettleAsync(
        Guid adminId, Guid eventId, Guid winningOutcomeId, CancellationToken ct = default)
        => runner.RunAsync(async token =>
        {
            var ev = await LockAndLoadEventAsync(eventId, token);

            switch (ev.Status)
            {
                case EventStatus.Settled:
                    throw new ConflictException("Event has already been settled.");
                case EventStatus.Voided:
                    throw new ConflictException("Event was voided and cannot be settled.");
                case EventStatus.Open:
                    throw new ConflictException("Event must be closed before it can be settled.");
            }

            if (ev.Outcomes.All(o => o.Id != winningOutcomeId))
                throw AppValidationException.For("winningOutcomeId", "The outcome does not belong to this event.");

            var bets = await LoadPlacedBetsAsync(ev.Id, token);
            var wallets = await LockAccountsAsync(ev.EscrowAccountId, bets, token);
            var now = DateTimeOffset.UtcNow;

            long totalStaked = 0, totalPaid = 0;
            int won = 0, lost = 0;

            foreach (var bet in bets)
            {
                totalStaked += bet.Stake;

                if (bet.OutcomeId == winningOutcomeId)
                {
                    var payout = Odds.Payout(bet.Stake, bet.OddsBp);   // uses the odds snapshotted at placement
                    await ledger.PostAsync(Postings.Payout(ev.EscrowAccountId, wallets[bet.UserId], payout, bet.Id), token);
                    bet.Payout = payout;
                    totalPaid += payout;
                    won++;
                    Transition(bet, BetStatus.Won, adminId, "event settled: winning outcome", now);
                }
                else
                {
                    bet.Payout = 0;
                    lost++;
                    Transition(bet, BetStatus.Lost, adminId, "event settled: losing outcome", now);
                }
            }

            var houseResult = await SweepEscrowAsync(ev, expectedResidual: totalStaked - totalPaid, token);

            var from = ev.Status;
            ev.Status = EventStatus.Settled;
            ev.WinningOutcomeId = winningOutcomeId;
            ev.SettledAt = now;
            db.EventHistory.Add(new EventStatusHistory
            {
                EventId = ev.Id, FromStatus = from, ToStatus = EventStatus.Settled,
                ActorId = adminId, Reason = "event settled", At = now
            });
            audit.Add(adminId, "event.settle", "event", ev.Id.ToString(), new
            {
                winningOutcomeId, winningBets = won, losingBets = lost, totalStaked, totalPaidOut = totalPaid, houseResult
            });
            outbox.Add("event.settled", new
            {
                eventId = ev.Id,
                adminId,
                winningOutcomeId,
                winningBets = won,
                losingBets = lost,
                totalStaked,
                totalPaidOut = totalPaid,
                houseResult
            });

            await db.SaveChangesAsync(token);

            return new SettlementResultDto(ev.Id, ev.Status, winningOutcomeId, won, lost, totalStaked, totalPaid, houseResult, now);
        }, ct);

    public Task<VoidResultDto> VoidAsync(Guid adminId, Guid eventId, string? reason, CancellationToken ct = default)
        => runner.RunAsync(async token =>
        {
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            if (reason is { Length: > 500 })
                throw AppValidationException.For("reason", "Reason must be at most 500 characters.");

            var ev = await LockAndLoadEventAsync(eventId, token);

            if (ev.Status == EventStatus.Settled)
                throw new ConflictException("Event has already been settled and cannot be voided.");
            if (ev.Status == EventStatus.Voided)
                throw new ConflictException("Event has already been voided.");

            var bets = await LoadPlacedBetsAsync(ev.Id, token);
            var wallets = await LockAccountsAsync(ev.EscrowAccountId, bets, token);
            var now = DateTimeOffset.UtcNow;

            long totalRefunded = 0;
            foreach (var bet in bets)
            {
                await ledger.PostAsync(Postings.Refund(ev.EscrowAccountId, wallets[bet.UserId], bet.Stake, bet.Id), token);
                totalRefunded += bet.Stake;
                Transition(bet, BetStatus.Refunded, adminId, reason ?? "event voided", now);
            }

            await SweepEscrowAsync(ev, expectedResidual: 0, token);   // after refunds the escrow must be empty

            var from = ev.Status;
            ev.Status = EventStatus.Voided;
            ev.SettledAt = now;
            db.EventHistory.Add(new EventStatusHistory
            {
                EventId = ev.Id, FromStatus = from, ToStatus = EventStatus.Voided,
                ActorId = adminId, Reason = reason ?? "event voided", At = now
            });
            audit.Add(adminId, "event.void", "event", ev.Id.ToString(), new
            {
                reason, betsRefunded = bets.Count, totalRefunded
            });
            outbox.Add("event.voided", new
            {
                eventId = ev.Id,
                adminId,
                reason,
                betsRefunded = bets.Count,
                totalRefunded
            });

            await db.SaveChangesAsync(token);

            return new VoidResultDto(ev.Id, ev.Status, bets.Count, totalRefunded, reason, now);
        }, ct);

    // ---------------------------------------------------------------- helpers

    private async Task<BettingEvent> LockAndLoadEventAsync(Guid eventId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM events WHERE id = {eventId} FOR UPDATE", ct);

        return await db.Events.Include(e => e.Outcomes).SingleOrDefaultAsync(e => e.Id == eventId, ct)
               ?? throw new NotFoundException("Event not found.");
    }

    private Task<List<Bet>> LoadPlacedBetsAsync(Guid eventId, CancellationToken ct)
        => db.Bets.Where(b => b.EventId == eventId && b.Status == BetStatus.Placed)
            .OrderBy(b => b.Id)
            .ToListAsync(ct);

    /// <summary>Locks escrow + House + every bettor's wallet in a single ordered statement; returns userId -> walletId.</summary>
    private async Task<Dictionary<Guid, Guid>> LockAccountsAsync(Guid escrowId, List<Bet> bets, CancellationToken ct)
    {
        var userIds = bets.Select(b => b.UserId).Distinct().ToList();

        var wallets = await db.Accounts.AsNoTracking()
            .Where(a => a.Type == AccountType.UserWallet && a.OwnerUserId != null && userIds.Contains(a.OwnerUserId.Value))
            .ToDictionaryAsync(a => a.OwnerUserId!.Value, a => a.Id, ct);

        if (userIds.Any(u => !wallets.ContainsKey(u)))
            throw new LedgerException("A bettor has no wallet; the event cannot be processed.");

        var ids = wallets.Values.Append(escrowId).Append(SystemAccounts.HouseId).Distinct().ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", ct);

        return wallets;
    }

    /// <summary>
    /// Verifies the escrow holds exactly what the bets say it should, then sweeps it to zero.
    /// Returns the residual (positive = house profit, negative = house loss).
    /// </summary>
    private async Task<long> SweepEscrowAsync(BettingEvent ev, long expectedResidual, CancellationToken ct)
    {
        var escrowBalance = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == ev.EscrowAccountId).Select(a => a.Balance).SingleAsync(ct);

        if (escrowBalance != expectedResidual)
            throw new InvalidOperationException(
                $"Escrow integrity check failed for event {ev.Id}: expected {expectedResidual}, found {escrowBalance}.");

        if (escrowBalance != 0)
            await ledger.PostAsync(SettlementPostings.Sweep(ev.EscrowAccountId, escrowBalance, ev.Id), ct);

        return escrowBalance;
    }

    private void Transition(Bet bet, BetStatus to, Guid actorId, string reason, DateTimeOffset at)
    {
        db.BetHistory.Add(new BetStatusHistory
        {
            BetId = bet.Id, FromStatus = bet.Status, ToStatus = to, ActorId = actorId, Reason = reason, At = at
        });
        bet.Status = to;
        bet.SettledAt = at;
    }
}
