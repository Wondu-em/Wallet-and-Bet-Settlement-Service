using Microsoft.EntityFrameworkCore;
using Wallet.Application.Ledger;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Ledger;

public sealed class LedgerService(WalletDbContext db) : ILedgerService
{
    public async Task<Guid> PostAsync(PostTransactionRequest request, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Ledger postings must run inside a database transaction. Use ITransactionRunner.");

        Validate(request);

        // Net effect per account (credit +, debit -).
        var deltas = new Dictionary<Guid, long>();
        foreach (var line in request.Lines)
        {
            var signed = line.Direction == EntryDirection.Credit ? line.Amount : -line.Amount;
            deltas[line.AccountId] = checked(deltas.GetValueOrDefault(line.AccountId) + signed);
        }
        var ids = deltas.Keys.ToArray();

        // 1) Lock all involved account rows in a deterministic order (by id) -> no deadlocks,
        //    and concurrent postings on the same wallet are serialised here.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", ct);

        // 2) Read balances AFTER the lock (fresh, untracked) and check funds.
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Type, a.Balance })
            .ToListAsync(ct);

        if (accounts.Count != ids.Length)
            throw new LedgerException("One or more accounts in the posting do not exist.");

        foreach (var a in accounts)
        {
            var delta = deltas[a.Id];
            if (a.Type == AccountType.UserWallet && checked(a.Balance + delta) < 0)
                throw new InsufficientFundsException(a.Id, a.Balance, -delta);
        }

        // 3) Append the transaction and its entries.
        var tx = new LedgerTransaction
        {
            Type = request.Type,
            ReferenceType = request.ReferenceType,
            ReferenceId = request.ReferenceId,
            Entries = request.Lines.Select(l => new LedgerEntry
            {
                AccountId = l.AccountId,
                Direction = l.Direction,
                Amount = l.Amount
            }).ToList()
        };
        db.LedgerTransactions.Add(tx);
        await db.SaveChangesAsync(ct);

        // 4) Update the cached balances (atomic increment, we hold the row locks).
        foreach (var (accountId, delta) in deltas)
        {
            if (delta == 0) continue;
            await db.Accounts.Where(a => a.Id == accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Balance, a => a.Balance + delta), ct);
        }

        return tx.Id;
    }

    public async Task<long> GetLedgerBalanceAsync(Guid accountId, CancellationToken ct = default)
    {
        var credits = await db.LedgerEntries
            .Where(e => e.AccountId == accountId && e.Direction == EntryDirection.Credit)
            .SumAsync(e => (long)e.Amount, ct);
        var debits = await db.LedgerEntries
            .Where(e => e.AccountId == accountId && e.Direction == EntryDirection.Debit)
            .SumAsync(e => (long)e.Amount, ct);
        return credits - debits;
    }

    private static void Validate(PostTransactionRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.ReferenceType))
            throw new LedgerException("ReferenceType is required.");
        if (r.Lines is null || r.Lines.Count < 2)
            throw new LedgerException("A ledger transaction needs at least two lines.");
        if (r.Lines.Any(l => l.Amount <= 0))
            throw new LedgerException("Every ledger line amount must be positive.");

        long debits, credits;
        try
        {
            debits = r.Lines.Where(l => l.Direction == EntryDirection.Debit).Sum(l => checked(l.Amount));
            credits = r.Lines.Where(l => l.Direction == EntryDirection.Credit).Sum(l => checked(l.Amount));
        }
        catch (OverflowException)
        {
            throw new LedgerException("Ledger amounts overflow.");
        }

        if (debits == 0 || credits == 0 || debits != credits)
            throw new LedgerException($"Unbalanced posting: debits {debits}, credits {credits}.");
    }
}
