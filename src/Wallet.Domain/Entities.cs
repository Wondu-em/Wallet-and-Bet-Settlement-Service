namespace Wallet.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Balance convention for every account: balance = SUM(credits) - SUM(debits).
/// Only UserWallet accounts are constrained to be non-negative.
/// </summary>
public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AccountType Type { get; set; }
    public Guid? OwnerUserId { get; set; }
    public long Balance { get; set; }
    public string Currency { get; set; } = "ETB";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class LedgerTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LedgerTransactionType Type { get; set; }
    public string ReferenceType { get; set; } = "";
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LedgerEntry> Entries { get; set; } = new();
}

public class LedgerEntry
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public EntryDirection Direction { get; set; }
    public long Amount { get; set; }
}

public class BettingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public EventStatus Status { get; set; } = EventStatus.Open;
    public Guid? WinningOutcomeId { get; set; }
    public Guid EscrowAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SettledAt { get; set; }
    public List<Outcome> Outcomes { get; set; } = new();
}

public class Outcome
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string Name { get; set; } = "";
    public int OddsBp { get; set; }
}

public class Bet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid EventId { get; set; }
    public Guid OutcomeId { get; set; }
    public long Stake { get; set; }
    public int OddsBp { get; set; }
    public BetStatus Status { get; set; } = BetStatus.Placed;
    public long? Payout { get; set; }
    public DateTimeOffset PlacedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SettledAt { get; set; }
}

public class EventStatusHistory
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public EventStatus? FromStatus { get; set; }
    public EventStatus ToStatus { get; set; }
    public Guid? ActorId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class BetStatusHistory
{
    public long Id { get; set; }
    public Guid BetId { get; set; }
    public BetStatus? FromStatus { get; set; }
    public BetStatus ToStatus { get; set; }
    public Guid? ActorId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class IdempotencyKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Scope { get; set; } = "";      // e.g. "user:{id}" or "admin:{id}" or "webhook"
    public string Endpoint { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.InProgress;
    public int? ResponseCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderEventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string RawPayload { get; set; } = "";
    public string? Result { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}

public class ProviderDeposit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderReference { get; set; } = "";
    public Guid UserId { get; set; }
    public long Amount { get; set; }
    public ProviderDepositStatus Status { get; set; }
    public Guid? LedgerTransactionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string Data { get; set; } = "{}";
    public string? CorrelationId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
