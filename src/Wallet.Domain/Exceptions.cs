namespace Wallet.Domain;

public abstract class DomainException(string message) : Exception(message);

/// <summary>The posting itself is malformed (unbalanced, non-positive amount, unknown account...).</summary>
public sealed class LedgerException(string message) : DomainException(message);

public sealed class InsufficientFundsException(Guid accountId, long available, long required)
    : DomainException($"Insufficient funds in account {accountId}: available {available}, required {required}.")
{
    public Guid AccountId { get; } = accountId;
    public long Available { get; } = available;
    public long Required { get; } = required;
}
