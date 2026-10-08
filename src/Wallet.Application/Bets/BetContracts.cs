using Wallet.Application.Wallets;
using Wallet.Domain;

namespace Wallet.Application.Bets;

public sealed record BetDto(
    Guid Id,
    Guid EventId,
    Guid OutcomeId,
    long Stake,
    int OddsBp,
    decimal Odds,
    BetStatus Status,
    long? Payout,
    long PotentialPayout,
    DateTimeOffset PlacedAt,
    DateTimeOffset? SettledAt);

public sealed record PlacedBetDto(BetDto Bet, long Balance);

public interface IBettingService
{
    /// <summary>
    /// Places a bet: locks the event (must be Open), debits the stake via the ledger, records the bet,
    /// its status history and an audit row, all in the caller's transaction.
    /// </summary>
    Task<PlacedBetDto> PlaceBetAsync(Guid userId, Guid outcomeId, long stake, CancellationToken ct = default);

    Task<PagedResult<BetDto>> ListBetsAsync(
        Guid userId, BetStatus? status, Guid? eventId, int page, int pageSize, CancellationToken ct = default);
}
