using Wallet.Domain;

namespace Wallet.Application.Settlement;

public sealed record SettlementResultDto(
    Guid EventId,
    EventStatus Status,
    Guid? WinningOutcomeId,
    int WinningBets,
    int LosingBets,
    long TotalStaked,
    long TotalPaidOut,
    long HouseResult,          // stakes - payouts: positive = house profit, negative = house loss
    DateTimeOffset SettledAt);

public sealed record VoidResultDto(
    Guid EventId,
    EventStatus Status,
    int BetsRefunded,
    long TotalRefunded,
    string? Reason,
    DateTimeOffset VoidedAt);

public interface ISettlementService
{
    /// <summary>
    /// Settles a Closed event: pays winners (stake x snapshotted odds, rounded down), marks losers,
    /// sweeps the escrow back to zero, records status history. Fully commits or fully rolls back.
    /// A second attempt fails with ConflictException.
    /// </summary>
    Task<SettlementResultDto> SettleAsync(Guid adminId, Guid eventId, Guid winningOutcomeId, CancellationToken ct = default);

    /// <summary>Voids an Open or Closed event and refunds every stake.</summary>
    Task<VoidResultDto> VoidAsync(Guid adminId, Guid eventId, string? reason, CancellationToken ct = default);
}
