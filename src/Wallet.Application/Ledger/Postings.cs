using Wallet.Domain;
using static Wallet.Domain.EntryDirection;

namespace Wallet.Application.Ledger;

/// <summary>
/// Posting rules from the architecture document. Balance convention: credit increases, debit decreases.
/// </summary>
public static class Postings
{
    public static PostTransactionRequest Deposit(Guid walletId, long amount, string referenceType = "deposit", Guid? referenceId = null)
        => new(LedgerTransactionType.Deposit, referenceType, referenceId, new PostingLine[]
        {
            new(SystemAccounts.ExternalClearingId, Debit, amount),
            new(walletId, Credit, amount)
        });

    public static PostTransactionRequest Withdraw(Guid walletId, long amount, string referenceType = "withdrawal", Guid? referenceId = null)
        => new(LedgerTransactionType.Withdraw, referenceType, referenceId, new PostingLine[]
        {
            new(walletId, Debit, amount),
            new(SystemAccounts.ExternalClearingId, Credit, amount)
        });

    public static PostTransactionRequest BetStake(Guid walletId, Guid escrowId, long stake, Guid betId)
        => new(LedgerTransactionType.BetStake, "bet", betId, new PostingLine[]
        {
            new(walletId, Debit, stake),
            new(escrowId, Credit, stake)
        });

    public static PostTransactionRequest Payout(Guid escrowId, Guid walletId, long amount, Guid betId)
        => new(LedgerTransactionType.Payout, "bet", betId, new PostingLine[]
        {
            new(escrowId, Debit, amount),
            new(walletId, Credit, amount)
        });

    public static PostTransactionRequest Refund(Guid escrowId, Guid walletId, long amount, Guid betId)
        => new(LedgerTransactionType.Refund, "bet", betId, new PostingLine[]
        {
            new(escrowId, Debit, amount),
            new(walletId, Credit, amount)
        });
}
