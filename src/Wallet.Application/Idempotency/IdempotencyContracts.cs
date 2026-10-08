using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wallet.Application.Idempotency;

public sealed record IdempotencyRequest(string Scope, string Endpoint, string Key, string RequestHash);

public sealed record IdempotentResponse(int StatusCode, string Body, bool Replayed = false)
{
    public static IdempotentResponse Of(int statusCode, object payload)
        => new(statusCode, JsonSerializer.Serialize(payload, AppJson.Options));
}

public interface IIdempotencyExecutor
{
    /// <summary>
    /// Runs <paramref name="work"/> at most once per (scope, endpoint, key).
    /// The key record, the business effect and the stored response commit or roll back together.
    /// Only 2xx responses are stored; failures roll everything back so the client may retry with the same key.
    /// Reusing a key with a different payload throws IdempotencyConflictException.
    /// </summary>
    Task<IdempotentResponse> ExecuteAsync(
        IdempotencyRequest request,
        Func<CancellationToken, Task<IdempotentResponse>> work,
        CancellationToken ct = default);
}

public static class RequestHasher
{
    public static string Hash<T>(T body)
    {
        var json = JsonSerializer.Serialize(body, AppJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
