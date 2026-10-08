using Wallet.Domain;
using static Wallet.Domain.EntryDirection;

namespace Wallet.Application.Ledger;

public static class SettlementPostings
{
    /// <summary>
    /// Moves whatever is left in an event's escrow to/from the House so the escrow ends at exactly zero.
    /// residual &gt; 0: stakes exceeded payouts, the house keeps the difference.
    /// residual &lt; 0: payouts exceeded stakes, the house covers the shortfall.
    /// </summary>
    public static PostTransactionRequest Sweep(Guid escrowId, long residual, Guid eventId)
    {
        if (residual == 0 || residual == long.MinValue)
            throw new ArgumentOutOfRangeException(nameof(residual), "Residual must be non-zero.");

        var amount = Math.Abs(residual);
        var lines = residual > 0
            ? new PostingLine[] { new(escrowId, Debit, amount), new(SystemAccounts.HouseId, Credit, amount) }
            : new PostingLine[] { new(SystemAccounts.HouseId, Debit, amount), new(escrowId, Credit, amount) };

        return new PostTransactionRequest(LedgerTransactionType.Sweep, "event", eventId, lines);
    }
}
