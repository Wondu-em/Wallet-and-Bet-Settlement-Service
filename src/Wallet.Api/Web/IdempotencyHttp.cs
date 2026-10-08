using Wallet.Application.Idempotency;
using Wallet.Domain;

namespace Wallet.Api.Web;

public static class IdempotencyHttp
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Reads the Idempotency-Key header and runs <paramref name="work"/> through the executor.</summary>
    public static async Task<IResult> RunAsync<TRequest>(
        HttpContext http,
        IIdempotencyExecutor executor,
        string endpoint,
        Guid userId,
        TRequest body,
        Func<CancellationToken, Task<IdempotentResponse>> work)
    {
        var key = http.Request.Headers[HeaderName].ToString().Trim();
        if (key.Length is 0 or > 200)
            throw AppValidationException.For(HeaderName, "The Idempotency-Key header is required (1 to 200 characters).");

        var request = new IdempotencyRequest($"user:{userId}", endpoint, key, RequestHasher.Hash(body));
        var response = await executor.ExecuteAsync(request, work, http.RequestAborted);

        if (response.Replayed)
            http.Response.Headers["Idempotent-Replayed"] = "true";

        return Results.Content(response.Body, "application/json", statusCode: response.StatusCode);
    }
}
