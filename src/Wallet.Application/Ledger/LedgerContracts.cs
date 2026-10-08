using Wallet.Domain;

namespace Wallet.Application.Ledger;

public sealed record PostingLine(Guid AccountId, EntryDirection Direction, long Amount);

public sealed record PostTransactionRequest(
    LedgerTransactionType Type,
    string ReferenceType,
    Guid? ReferenceId,
    IReadOnlyList<PostingLine> Lines);

public interface ILedgerService
{
    /// <summary>
    /// Posts one balanced ledger transaction and updates cached balances.
    /// MUST be called inside a database transaction (see <see cref="ITransactionRunner"/>);
    /// the caller owns commit/rollback so a bet, its ledger entries and its history commit together.
    /// </summary>
    Task<Guid> PostAsync(PostTransactionRequest request, CancellationToken ct = default);

    /// <summary>Balance derived purely from the ledger: SUM(credits) - SUM(debits).</summary>
    Task<long> GetLedgerBalanceAsync(Guid accountId, CancellationToken ct = default);
}

public interface ITransactionRunner
{
    /// <summary>Runs <paramref name="work"/> in one DB transaction (joins an existing one if present).</summary>
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
}
