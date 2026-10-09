using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wallet.Api.Security;
using Wallet.Application;
using Wallet.Application.Idempotency;
using Wallet.Application.Webhooks;
using Wallet.Domain;
using Wallet.Infrastructure.Audit;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Ledger;
using Wallet.Infrastructure.Outbox;
using Wallet.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Wallet.IntegrationTests;

[Collection("PostgreSQL integration")]
public class DepositWebhookTests(PostgresFixture fx)
{
    private const string Endpoint = "POST /webhooks/deposit";

    private async Task<IdempotentResponse> ProcessAsync(
        DepositWebhookRequest request,
        string idempotencyKey,
        string? rawPayload = null)
    {
        rawPayload ??= JsonSerializer.Serialize(request, AppJson.Options);
        await using var db = fx.CreateContext();
        var executor = new IdempotencyExecutor(db);
        var service = new DepositWebhookService(db, new LedgerService(db), new AuditWriter(db), new OutboxWriter(db));
        return await executor.ExecuteAsync(
            new IdempotencyRequest("webhook", Endpoint, idempotencyKey, Hash(rawPayload)),
            async ct => IdempotentResponse.Of(200,
                await service.ProcessDepositAsync(request, rawPayload, ct)));
    }

    [Fact]
    public async Task Confirmed_deposit_is_credited_once_and_late_pending_is_ignored()
    {
        var walletId = await fx.CreateWalletAsync();
        var userId = await GetOwnerAsync(walletId);
        var reference = Guid.NewGuid().ToString("N");

        var confirmed = new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 12_500, ProviderDepositStatus.Confirmed);
        var pending = new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 12_500, ProviderDepositStatus.Pending);

        var result = await ProcessAsync(confirmed, Guid.NewGuid().ToString("N"));
        var lateResult = await ProcessAsync(pending, Guid.NewGuid().ToString("N"));

        Assert.False(result.Replayed);
        Assert.False(lateResult.Replayed);
        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(12_500, cached);
        Assert.Equal(12_500, derived);
        await using var db = fx.CreateContext();
        var deposit = await db.ProviderDeposits.SingleAsync(d => d.ProviderReference == reference);
        Assert.Equal(ProviderDepositStatus.Confirmed, deposit.Status);
        Assert.NotNull(deposit.LedgerTransactionId);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Repeated_provider_event_with_a_different_idempotency_key_is_deduplicated()
    {
        var walletId = await fx.CreateWalletAsync();
        var userId = await GetOwnerAsync(walletId);
        var request = new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            userId, 4_000, ProviderDepositStatus.Confirmed);
        var rawPayload = JsonSerializer.Serialize(request, AppJson.Options);

        var key = Guid.NewGuid().ToString("N");
        var first = await ProcessAsync(request, key, rawPayload);
        var retry = await ProcessAsync(request, key, rawPayload);
        var duplicate = await ProcessAsync(request, Guid.NewGuid().ToString("N"), rawPayload);

        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.False(duplicate.Replayed);
        Assert.Contains("\"duplicate\":true", duplicate.Body);
        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(4_000, cached);
        Assert.Equal(4_000, derived);
    }

    [Fact]
    public async Task Pending_deposit_is_credited_when_a_later_event_confirms_it()
    {
        var walletId = await fx.CreateWalletAsync();
        var userId = await GetOwnerAsync(walletId);
        var reference = Guid.NewGuid().ToString("N");

        await ProcessAsync(new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 6_000, ProviderDepositStatus.Pending),
            Guid.NewGuid().ToString("N"));
        await ProcessAsync(new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 6_000, ProviderDepositStatus.Confirmed),
            Guid.NewGuid().ToString("N"));

        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(6_000, cached);
        Assert.Equal(6_000, derived);
        await using var db = fx.CreateContext();
        Assert.Equal(ProviderDepositStatus.Confirmed,
            await db.ProviderDeposits.Where(d => d.ProviderReference == reference)
                .Select(d => d.Status).SingleAsync());
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Concurrent_repeated_provider_event_is_credited_once()
    {
        var walletId = await fx.CreateWalletAsync();
        var userId = await GetOwnerAsync(walletId);
        var request = new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            userId, 9_000, ProviderDepositStatus.Confirmed);
        var rawPayload = JsonSerializer.Serialize(request, AppJson.Options);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => ProcessAsync(request, $"key-{Guid.NewGuid():N}", rawPayload))));

        Assert.All(responses, response => Assert.Equal(200, response.StatusCode));
        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(9_000, cached);
        Assert.Equal(9_000, derived);
        await using var db = fx.CreateContext();
        Assert.Equal(1, await db.WebhookDeliveries.CountAsync(d => d.ProviderEventId == request.ProviderEventId));
        Assert.Equal(1, await db.ProviderDeposits.CountAsync(d => d.ProviderReference == request.ProviderReference));
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Failed_deposit_cannot_be_confirmed_later()
    {
        var walletId = await fx.CreateWalletAsync();
        var userId = await GetOwnerAsync(walletId);
        var reference = Guid.NewGuid().ToString("N");

        await ProcessAsync(new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 2_000, ProviderDepositStatus.Failed),
            Guid.NewGuid().ToString("N"));
        await ProcessAsync(new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 2_000, ProviderDepositStatus.Confirmed),
            Guid.NewGuid().ToString("N"));

        var (cached, derived) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(0, cached);
        Assert.Equal(0, derived);
        await using var db = fx.CreateContext();
        var deposit = await db.ProviderDeposits.SingleAsync(d => d.ProviderReference == reference);
        Assert.Equal(ProviderDepositStatus.Failed, deposit.Status);
        Assert.Null(deposit.LedgerTransactionId);
    }

    [Fact]
    public async Task Reusing_provider_reference_with_different_amount_is_rejected_without_effect()
    {
        var walletId = await fx.CreateWalletAsync();
        var userId = await GetOwnerAsync(walletId);
        var reference = Guid.NewGuid().ToString("N");

        await ProcessAsync(new DepositWebhookRequest(
            Guid.NewGuid().ToString("N"), reference, userId, 2_000, ProviderDepositStatus.Pending),
            Guid.NewGuid().ToString("N"));

        var conflictingEvent = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<ConflictException>(() =>
            ProcessAsync(new DepositWebhookRequest(
                conflictingEvent, reference, userId, 3_000, ProviderDepositStatus.Confirmed),
                Guid.NewGuid().ToString("N")));

        var (cached, _) = await fx.GetBalancesAsync(walletId);
        Assert.Equal(0, cached);
        await using var db = fx.CreateContext();
        Assert.False(await db.WebhookDeliveries.AnyAsync(d => d.ProviderEventId == conflictingEvent));
        Assert.Equal(ProviderDepositStatus.Pending,
            await db.ProviderDeposits.Where(d => d.ProviderReference == reference)
                .Select(d => d.Status).SingleAsync());
    }

    [Fact]
    public void Signature_verification_requires_exact_body_and_fresh_timestamp()
    {
        const string secret = "test-webhook-secret-with-at-least-32-bytes";
        var verifier = new DepositWebhookSignatureVerifier(secret);
        var body = Encoding.UTF8.GetBytes("{\"status\":\"Confirmed\"}");
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = Sign(secret, timestamp, body);

        Assert.True(verifier.IsValid(body, timestamp, signature, now));
        Assert.False(verifier.IsValid(Encoding.UTF8.GetBytes("{\"status\":\"Pending\"}"),
            timestamp, signature, now));
        Assert.False(verifier.IsValid(body, (now - TimeSpan.FromMinutes(6)).ToUnixTimeSeconds().ToString(),
            signature, now));
        Assert.False(verifier.IsValid(body, timestamp, "not-a-signature", now));
    }

    private async Task<Guid> GetOwnerAsync(Guid walletId)
    {
        await using var db = fx.CreateContext();
        return await db.Accounts.Where(a => a.Id == walletId).Select(a => a.OwnerUserId!.Value).SingleAsync();
    }

    private static string Hash(string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static string Sign(string secret, string timestamp, byte[] body)
    {
        var message = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body)}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }
}
