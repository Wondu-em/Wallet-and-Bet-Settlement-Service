using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wallet.Application;
using Wallet.Application.Audit;
using Wallet.Application.Ledger;
using Wallet.Application.Outbox;
using Wallet.Application.Webhooks;
using Wallet.Domain;
using Wallet.Infrastructure.Ledger;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Services;

public sealed class DepositWebhookService(
    WalletDbContext db,
    ILedgerService ledger,
    IAuditWriter audit,
    IOutboxWriter outbox) : IDepositWebhookService
{
    public async Task<DepositWebhookResult> ProcessDepositAsync(
        DepositWebhookRequest request,
        string rawPayload,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Webhook processing must run inside the idempotency transaction.");

        Validate(request);

        var eventType = $"deposit.{request.Status.ToString().ToLowerInvariant()}";
        var receivedAt = DateTimeOffset.UtcNow;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO webhook_deliveries
                (id, provider_event_id, event_type, raw_payload, received_at)
            VALUES
                ({Guid.NewGuid()}, {request.ProviderEventId}, {eventType}, {rawPayload}, {receivedAt})
            ON CONFLICT (provider_event_id) DO NOTHING
            """, ct);

        if (inserted == 0)
        {
            var existingDelivery = await db.WebhookDeliveries.AsNoTracking()
                .SingleAsync(d => d.ProviderEventId == request.ProviderEventId, ct);
            var existingRequest = JsonSerializer.Deserialize<DepositWebhookRequest>(
                Encoding.UTF8.GetBytes(existingDelivery.RawPayload), AppJson.Options);
            if (existingRequest != request)
                throw new ConflictException("Provider event id was already used with a different payload.");

            var existingDeposit = await db.ProviderDeposits.AsNoTracking()
                .SingleAsync(d => d.ProviderReference == request.ProviderReference, ct);
            return new DepositWebhookResult(existingDeposit.ProviderReference, existingDeposit.Status,
                Duplicate: true, StateChanged: false);
        }

        var now = DateTimeOffset.UtcNow;
        var initialStatus = ProviderDepositStatus.Pending.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_deposits
                (id, provider_reference, user_id, amount, status, ledger_transaction_id, created_at, updated_at)
            VALUES
                ({Guid.NewGuid()}, {request.ProviderReference}, {request.UserId}, {request.Amount},
                 {initialStatus}, {null}, {now}, {now})
            ON CONFLICT (provider_reference) DO NOTHING
            """, ct);

        var deposit = await db.ProviderDeposits
            .FromSqlInterpolated(
                $"SELECT * FROM provider_deposits WHERE provider_reference = {request.ProviderReference} FOR UPDATE")
            .SingleAsync(ct);

        if (deposit.UserId != request.UserId || deposit.Amount != request.Amount)
            throw new ConflictException(
                "Provider reference was already used with a different user or amount.");

        var previousStatus = deposit.Status;
        var stateChanged = inserted == 1;
        if (previousStatus == ProviderDepositStatus.Pending &&
            request.Status != ProviderDepositStatus.Pending)
        {
            if (request.Status == ProviderDepositStatus.Confirmed)
            {
                var wallet = await db.Accounts.AsNoTracking()
                    .SingleOrDefaultAsync(a =>
                        a.OwnerUserId == request.UserId && a.Type == AccountType.UserWallet, ct)
                    ?? throw new NotFoundException("Wallet not found for confirmed provider deposit.");

                deposit.LedgerTransactionId = await ledger.PostAsync(
                    Postings.Deposit(wallet.Id, deposit.Amount, "provider_deposit", deposit.Id), ct);
            }

            deposit.Status = request.Status;
            deposit.UpdatedAt = DateTimeOffset.UtcNow;
            stateChanged = true;
            audit.Add(null, $"deposit.{request.Status.ToString().ToLowerInvariant()}",
                "provider_deposit", deposit.Id.ToString(),
                new
                {
                    request.ProviderEventId,
                    request.ProviderReference,
                    previousStatus,
                    status = request.Status,
                    request.Amount
                });
        }
        else if (previousStatus == ProviderDepositStatus.Pending && inserted == 1)
        {
            audit.Add(null, "deposit.pending", "provider_deposit", deposit.Id.ToString(),
                new { request.ProviderEventId, request.ProviderReference, request.Amount });
        }

        var result = stateChanged
            ? request.Status == ProviderDepositStatus.Pending ? "pending" : "state_changed"
            : "ignored_stale_or_duplicate";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE webhook_deliveries
               SET result = {result}, processed_at = {DateTimeOffset.UtcNow}
             WHERE provider_event_id = {request.ProviderEventId}
            """, ct);
        outbox.Add("deposit.provider_event.processed", new
        {
            request.ProviderEventId,
            request.ProviderReference,
            request.UserId,
            request.Amount,
            receivedStatus = request.Status,
            resultingStatus = deposit.Status,
            depositId = deposit.Id,
            stateChanged
        });
        await db.SaveChangesAsync(ct);

        return new DepositWebhookResult(deposit.ProviderReference, deposit.Status,
            Duplicate: false, StateChanged: stateChanged);
    }

    private static void Validate(DepositWebhookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderEventId) || request.ProviderEventId.Length > 200)
            throw AppValidationException.For(nameof(request.ProviderEventId),
                "Provider event id must contain 1 to 200 characters.");
        if (string.IsNullOrWhiteSpace(request.ProviderReference) || request.ProviderReference.Length > 200)
            throw AppValidationException.For(nameof(request.ProviderReference),
                "Provider reference must contain 1 to 200 characters.");
        if (request.UserId == Guid.Empty)
            throw AppValidationException.For(nameof(request.UserId), "User id is required.");
        if (request.Amount <= 0 || request.Amount > MoneyLimits.MaxSingleAmount)
            throw AppValidationException.For(nameof(request.Amount),
                $"Amount must be between 1 and {MoneyLimits.MaxSingleAmount} minor units.");
        if (!Enum.IsDefined(request.Status))
            throw AppValidationException.For(nameof(request.Status), "Deposit status is invalid.");
    }
}
