using Wallet.Domain;

namespace Wallet.Application.Webhooks;

public sealed record DepositWebhookRequest(
    string ProviderEventId,
    string ProviderReference,
    Guid UserId,
    long Amount,
    ProviderDepositStatus Status);

public sealed record DepositWebhookResult(
    string ProviderReference,
    ProviderDepositStatus Status,
    bool Duplicate,
    bool StateChanged);

public interface IDepositWebhookService
{
    Task<DepositWebhookResult> ProcessDepositAsync(
        DepositWebhookRequest request,
        string rawPayload,
        CancellationToken ct = default);
}
