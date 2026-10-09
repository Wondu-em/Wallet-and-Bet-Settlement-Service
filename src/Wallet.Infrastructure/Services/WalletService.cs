using Microsoft.EntityFrameworkCore;
using Wallet.Application.Audit;
using Wallet.Application.Ledger;
using Wallet.Application.Outbox;
using Wallet.Application.Wallets;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Services;

public sealed class WalletService(
    WalletDbContext db,
    ILedgerService ledger,
    ITransactionRunner runner,
    IAuditWriter audit,
    IOutboxWriter outbox) : IWalletService
{
    public async Task<WalletBalanceDto> GetBalanceAsync(Guid userId, CancellationToken ct = default)
    {
        var wallet = await FindWalletAsync(userId, ct);
        return new WalletBalanceDto(wallet.Id, wallet.Balance, wallet.Currency);
    }

    public Task<MoneyMovementDto> DepositAsync(Guid userId, long amount, CancellationToken ct = default)
        => runner.RunAsync(async token =>
        {
            ValidateAmount(amount);
            var wallet = await FindWalletAsync(userId, token);
            var reference = Guid.NewGuid();

            var txId = await ledger.PostAsync(Postings.Deposit(wallet.Id, amount, "deposit", reference), token);
            audit.Add(userId, "wallet.deposit", "ledger_transaction", txId.ToString(), new { amount });
            outbox.Add("wallet.deposit.completed", new { userId, transactionId = txId, amount });
            await db.SaveChangesAsync(token);

            return new MoneyMovementDto(txId, amount, await ReadBalanceAsync(wallet.Id, token));
        }, ct);

    public Task<MoneyMovementDto> WithdrawAsync(Guid userId, long amount, CancellationToken ct = default)
        => runner.RunAsync(async token =>
        {
            ValidateAmount(amount);
            var wallet = await FindWalletAsync(userId, token);
            var reference = Guid.NewGuid();

            // The ledger locks the wallet row and rejects the posting if funds are insufficient.
            var txId = await ledger.PostAsync(Postings.Withdraw(wallet.Id, amount, "withdrawal", reference), token);
            audit.Add(userId, "wallet.withdraw", "ledger_transaction", txId.ToString(), new { amount });
            outbox.Add("wallet.withdrawal.completed", new { userId, transactionId = txId, amount });
            await db.SaveChangesAsync(token);

            return new MoneyMovementDto(txId, amount, await ReadBalanceAsync(wallet.Id, token));
        }, ct);

    public async Task<PagedResult<TransactionDto>> GetTransactionsAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var wallet = await FindWalletAsync(userId, ct);

        var query = db.LedgerEntries.AsNoTracking()
            .Where(e => e.AccountId == wallet.Id)
            .Join(db.LedgerTransactions.AsNoTracking(), e => e.TransactionId, t => t.Id, (e, t) => new { e, t });

        var total = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.t.CreatedAt).ThenByDescending(x => x.e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new TransactionDto(
                x.t.Id, x.t.Type, x.e.Direction, x.e.Amount, x.t.ReferenceType, x.t.ReferenceId, x.t.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<TransactionDto>(items, page, pageSize, total);
    }

    private static void ValidateAmount(long amount)
    {
        if (amount <= 0 || amount > MoneyLimits.MaxSingleAmount)
            throw AppValidationException.For("amount",
                $"Amount must be between 1 and {MoneyLimits.MaxSingleAmount} (minor units, 100 = 1.00 ETB).");
    }

    private async Task<Account> FindWalletAsync(Guid userId, CancellationToken ct)
        => await db.Accounts.AsNoTracking()
               .SingleOrDefaultAsync(a => a.OwnerUserId == userId && a.Type == AccountType.UserWallet, ct)
           ?? throw new NotFoundException("Wallet not found.");

    private Task<long> ReadBalanceAsync(Guid walletId, CancellationToken ct)
        => db.Accounts.AsNoTracking().Where(a => a.Id == walletId).Select(a => a.Balance).SingleAsync(ct);
}
