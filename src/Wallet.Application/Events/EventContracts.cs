using Wallet.Application.Wallets;
using Wallet.Domain;

namespace Wallet.Application.Events;

public sealed record OutcomeInput(string Name, decimal Odds);

public sealed record OutcomeDto(Guid Id, string Name, decimal Odds, int OddsBp);

public sealed record EventDto(
    Guid Id,
    string Title,
    EventStatus Status,
    Guid? WinningOutcomeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt,
    IReadOnlyList<OutcomeDto> Outcomes);

public interface IEventService
{
    /// <summary>Creates an event with 2 to 3 outcomes at fixed odds, plus its escrow account.</summary>
    Task<EventDto> CreateAsync(Guid adminId, string title, IReadOnlyList<OutcomeInput> outcomes, CancellationToken ct = default);

    /// <summary>Closes betting. Repeating the call on an already-closed event is a harmless no-op.</summary>
    Task<EventDto> CloseAsync(Guid adminId, Guid eventId, CancellationToken ct = default);

    Task<EventDto> GetAsync(Guid eventId, CancellationToken ct = default);

    Task<PagedResult<EventDto>> ListAsync(EventStatus? status, int page, int pageSize, CancellationToken ct = default);
}
