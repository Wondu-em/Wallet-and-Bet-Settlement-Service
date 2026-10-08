using Wallet.Domain;

namespace Wallet.Application.Wallets;

public sealed record WalletBalanceDto(Guid WalletId, long Balance, string Currency);

public sealed record MoneyMovementDto(Guid TransactionId, long Amount, long Balance);

public sealed record TransactionDto(
    Guid TransactionId,
    LedgerTransactionType Type,
    EntryDirection Direction,   // Credit = money in, Debit = money out (from the wallet's perspective)
    long Amount,
    string ReferenceType,
    Guid? ReferenceId,
    DateTimeOffset CreatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public interface IWalletService
{
    Task<WalletBalanceDto> GetBalanceAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Joins the caller's transaction (see IIdempotencyExecutor) or opens its own.</summary>
    Task<MoneyMovementDto> DepositAsync(Guid userId, long amount, CancellationToken ct = default);

    Task<MoneyMovementDto> WithdrawAsync(Guid userId, long amount, CancellationToken ct = default);

    Task<PagedResult<TransactionDto>> GetTransactionsAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
}
