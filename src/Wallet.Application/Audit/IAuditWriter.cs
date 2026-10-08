namespace Wallet.Application.Audit;

public interface IAuditWriter
{
    /// <summary>
    /// Stages an audit row in the current DbContext. It is persisted by the next SaveChanges,
    /// i.e. inside the same transaction as the business change.
    /// </summary>
    void Add(Guid? actorId, string action, string entityType, string? entityId, object? data = null);
}
