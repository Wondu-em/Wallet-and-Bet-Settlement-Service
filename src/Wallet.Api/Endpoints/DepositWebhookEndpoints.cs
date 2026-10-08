using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Security;
using Wallet.Application;
using Wallet.Application.Idempotency;
using Wallet.Application.Webhooks;
using Wallet.Domain;

namespace Wallet.Api.Endpoints;

public static class DepositWebhookEndpoints
{
    private const int MaxRequestBodyBytes = 64 * 1024;
    private const string EndpointName = "POST /webhooks/deposit";

    public static void MapDepositWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/deposit", async (
            HttpRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            DepositWebhookSignatureVerifier signatureVerifier,
            IIdempotencyExecutor idempotency,
            IDepositWebhookService deposits,
            CancellationToken ct) =>
        {
            var rawBody = await ReadRawBodyAsync(request, ct);
            var timestamp = request.Headers["X-Timestamp"].ToString();
            var signature = request.Headers["X-Signature"].ToString();
            if (!signatureVerifier.IsValid(rawBody, timestamp, signature, DateTimeOffset.UtcNow))
                throw new UnauthorizedAccessException("Invalid webhook signature or timestamp.");

            var key = idempotencyKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > 200)
                throw AppValidationException.For("Idempotency-Key",
                    "The Idempotency-Key header is required (1 to 200 characters).");

            string rawPayload;
            try
            {
                rawPayload = new UTF8Encoding(false, true).GetString(rawBody);
            }
            catch (DecoderFallbackException)
            {
                throw AppValidationException.For("body", "Request body must be valid UTF-8 JSON.");
            }

            DepositWebhookRequest body;
            try
            {
                body = JsonSerializer.Deserialize<DepositWebhookRequest>(rawBody, AppJson.Options)
                    ?? throw AppValidationException.For("body", "A JSON request body is required.");
            }
            catch (JsonException)
            {
                throw AppValidationException.For("body", "Request body must be valid deposit webhook JSON.");
            }

            var rawHash = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
            var response = await idempotency.ExecuteAsync(
                new IdempotencyRequest("webhook", EndpointName, key, rawHash),
                async token => IdempotentResponse.Of(StatusCodes.Status200OK,
                    await deposits.ProcessDepositAsync(body, rawPayload, token)),
                ct);

            if (response.Replayed)
                request.HttpContext.Response.Headers["Idempotent-Replayed"] = "true";

            return Results.Content(response.Body, "application/json", statusCode: response.StatusCode);
        })
        .AllowAnonymous()
        .WithTags("Webhooks")
        .WithSummary("Receive a signed provider deposit event")
        .WithDescription(
            "Requires Idempotency-Key, X-Timestamp (Unix seconds), and X-Signature (lowercase or uppercase " +
            "hex HMAC-SHA256 of `timestamp + \".\" + raw request body`). Requests older or newer than five minutes are rejected.")
        .Accepts<DepositWebhookRequest>("application/json")
        .Produces<DepositWebhookResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<byte[]> ReadRawBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRequestBodyBytes)
            throw AppValidationException.For("body", $"Request body cannot exceed {MaxRequestBodyBytes} bytes.");

        using var body = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;
            if (body.Length + read > MaxRequestBodyBytes)
                throw AppValidationException.For("body", $"Request body cannot exceed {MaxRequestBodyBytes} bytes.");

            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return body.ToArray();
    }
}
