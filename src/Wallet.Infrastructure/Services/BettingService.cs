using Microsoft.EntityFrameworkCore;
using Wallet.Application.Audit;
using Wallet.Application.Bets;
using Wallet.Application.Ledger;
using Wallet.Application.Outbox;
using Wallet.Application.Wallets;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Services;

public sealed class BettingService(
    WalletDbContext db,
    ILedgerService ledger,
    ITransactionRunner runner,
    IAuditWriter audit,
    IOutboxWriter outbox) : IBettingService
{
    public Task<PlacedBetDto> PlaceBetAsync(Guid userId, Guid outcomeId, long stake, CancellationToken ct = default)
        => runner.RunAsync(async token =>
        {
            if (stake <= 0 || stake > MoneyLimits.MaxSingleAmount)
                throw AppValidationException.For("stake",
                    $"Stake must be between 1 and {MoneyLimits.MaxSingleAmount} (minor units, 100 = 1.00 ETB).");

            var outcome = await db.Outcomes.AsNoTracking().SingleOrDefaultAsync(o => o.Id == outcomeId, token)
                          ?? throw new NotFoundException("Outcome not found.");

            // Shared lock on the event row. Many bets can hold it together, but close/settle/void
            // (FOR UPDATE) must wait for them, and bets that start after a close see "Closed".
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM events WHERE id = {outcome.EventId} FOR SHARE", token);

            var ev = await db.Events.AsNoTracking().SingleAsync(e => e.Id == outcome.EventId, token);
            if (ev.Status != EventStatus.Open)
                throw new ConflictException("Event is not open for betting.");

            var wallet = await db.Accounts.AsNoTracking()
                             .SingleOrDefaultAsync(a => a.OwnerUserId == userId && a.Type == AccountType.UserWallet, token)
                         ?? throw new NotFoundException("Wallet not found.");

            var bet = new Bet
            {
                UserId = userId,
                EventId = ev.Id,
                OutcomeId = outcome.Id,
                Stake = stake,
                OddsBp = outcome.OddsBp,          // odds are snapshotted at placement
                Status = BetStatus.Placed
            };
            db.Bets.Add(bet);
            db.BetHistory.Add(new BetStatusHistory
            {
                BetId = bet.Id, FromStatus = null, ToStatus = BetStatus.Placed, ActorId = userId, Reason = "bet placed"
            });
            audit.Add(userId, "bet.place", "bet", bet.Id.ToString(),
                new { bet.EventId, bet.OutcomeId, stake, bet.OddsBp });
            outbox.Add("bet.placed", new
            {
                betId = bet.Id,
                userId,
                eventId = ev.Id,
                outcomeId = outcome.Id,
                stake,
                oddsBp = bet.OddsBp
            });

            // Locks wallet + escrow, rejects with InsufficientFundsException BEFORE anything is saved,
            // then writes the bet, history, audit and ledger entries together.
            await ledger.PostAsync(Postings.BetStake(wallet.Id, ev.EscrowAccountId, stake, bet.Id), token);

            var balance = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == wallet.Id).Select(a => a.Balance).SingleAsync(token);

            return new PlacedBetDto(ToDto(bet), balance);
        }, ct);

    public async Task<PagedResult<BetDto>> ListBetsAsync(
        Guid userId, BetStatus? status, Guid? eventId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Bets.AsNoTracking().Where(b => b.UserId == userId);
        if (status is not null) query = query.Where(b => b.Status == status);
        if (eventId is not null) query = query.Where(b => b.EventId == eventId);

        var total = await query.LongCountAsync(ct);
        var bets = await query
            .OrderByDescending(b => b.PlacedAt).ThenByDescending(b => b.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<BetDto>(bets.Select(ToDto).ToList(), page, pageSize, total);
    }

    private static BetDto ToDto(Bet b) => new(
        b.Id, b.EventId, b.OutcomeId, b.Stake, b.OddsBp, b.OddsBp / (decimal)Odds.Scale,
        b.Status, b.Payout, Odds.Payout(b.Stake, b.OddsBp), b.PlacedAt, b.SettledAt);
}
