using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Security;
using Wallet.Api.Web;
using Wallet.Application.Bets;
using Wallet.Application.Idempotency;
using Wallet.Domain;

namespace Wallet.Api.Endpoints;

public sealed record PlaceBetRequest(Guid OutcomeId, long Stake);

public static class BetEndpoints
{
    public static void MapBetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bets").WithTags("Bets").RequireAuthorization("User");

        group.MapPost("/", async (
            PlaceBetRequest req,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,   // shown in Swagger; read by IdempotencyHttp
            HttpContext http, ClaimsPrincipal principal,
            IIdempotencyExecutor idempotency, IBettingService betting) =>
        {
            var userId = principal.GetUserId();
            return await IdempotencyHttp.RunAsync(http, idempotency, "POST /bets", userId, req, async ct =>
            {
                var placed = await betting.PlaceBetAsync(userId, req.OutcomeId, req.Stake, ct);
                return IdempotentResponse.Of(StatusCodes.Status201Created, placed);
            });
        }).RequireRateLimiting("bets");

        group.MapGet("/", async (
            BetStatus? status, Guid? eventId, int? page, int? pageSize,
            ClaimsPrincipal principal, IBettingService betting, CancellationToken ct)
            => Results.Ok(await betting.ListBetsAsync(principal.GetUserId(), status, eventId, page ?? 1, pageSize ?? 20, ct)));
    }
}