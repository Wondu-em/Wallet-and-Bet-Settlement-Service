using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Wallet.Domain;

namespace Wallet.Api.Web;

/// <summary>Maps exceptions to RFC 9457 problem responses with a stable machine-readable "code".</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        (int Status, string Code, string Title, IDictionary<string, string[]>? Errors) r = exception switch
        {
            AppValidationException v        => (400, "validation_failed", "Validation failed", v.Errors),
            InsufficientFundsException      => (422, "insufficient_funds", "Insufficient funds", null),
            IdempotencyConflictException    => (422, "idempotency_key_reuse", "Idempotency-Key reuse with a different payload", null),
            ConflictException               => (409, "conflict", "Conflict", null),
            NotFoundException               => (404, "not_found", "Not found", null),
            InvalidCredentialsException     => (401, "invalid_credentials", "Invalid credentials", null),
            UnauthorizedAccessException     => (401, "unauthorized", "Unauthorized", null),
            _                               => (500, "internal_error", "An unexpected error occurred", null)
        };

        if (r.Status >= 500)
            logger.LogError(exception, "Unhandled exception");
        else
            logger.LogInformation("Request rejected: {Code} ({Message})", r.Code, exception.Message);

        var problem = new ProblemDetails
        {
            Status = r.Status,
            Title = r.Title,
            Detail = r.Status < 500 ? exception.Message : null
        };
        problem.Extensions["code"] = r.Code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        if (r.Errors is not null) problem.Extensions["errors"] = r.Errors;

        context.Response.StatusCode = r.Status;
        await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
        return true;
    }
}
