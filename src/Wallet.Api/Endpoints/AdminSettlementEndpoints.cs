using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Security;
using Wallet.Api.Web;
using Wallet.Application.Idempotency;
using Wallet.Application.Settlement;

namespace Wallet.Api.Endpoints;

public sealed record SettleEventRequest(Guid WinningOutcomeId);
public sealed record VoidEventRequest(string? Reason);

public static class AdminSettlementEndpoints
{
    public static void MapAdminSettlementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/events").WithTags("Admin: Settlement").RequireAuthorization("Admin");

        group.MapPost("/{id:guid}/settle", async (
            Guid id,
            SettleEventRequest req,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,   // shown in Swagger; read by IdempotencyHttp
            HttpContext http, ClaimsPrincipal principal,
            IIdempotencyExecutor idempotency, ISettlementService settlement) =>
        {
            var adminId = principal.GetUserId();
            return await IdempotencyHttp.RunAsync(
                http, idempotency, $"POST /admin/events/{id}/settle", adminId,
                new { id, req.WinningOutcomeId },
                async ct => IdempotentResponse.Of(StatusCodes.Status200OK,
                    await settlement.SettleAsync(adminId, id, req.WinningOutcomeId, ct)));
        });

        group.MapPost("/{id:guid}/void", async (
            Guid id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            HttpContext http, ClaimsPrincipal principal,
            IIdempotencyExecutor idempotency, ISettlementService settlement,
            VoidEventRequest? req = null) =>
        {
            var adminId = principal.GetUserId();
            return await IdempotencyHttp.RunAsync(
                http, idempotency, $"POST /admin/events/{id}/void", adminId,
                new { id, reason = req?.Reason },
                async ct => IdempotentResponse.Of(StatusCodes.Status200OK,
                    await settlement.VoidAsync(adminId, id, req?.Reason, ct)));
        });
    }
}
