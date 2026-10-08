using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Security;
using Wallet.Api.Web;
using Wallet.Application.Idempotency;
using Wallet.Application.Wallets;

namespace Wallet.Api.Endpoints;

public sealed record AmountRequest(long Amount);

public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/wallet").WithTags("Wallet").RequireAuthorization("User");

        group.MapGet("/", async (ClaimsPrincipal principal, IWalletService wallet, CancellationToken ct)
            => Results.Ok(await wallet.GetBalanceAsync(principal.GetUserId(), ct)));

        group.MapPost("/deposit", async (
            AmountRequest req,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,   // shown in Swagger; read by IdempotencyHttp
            HttpContext http, ClaimsPrincipal principal,
            IIdempotencyExecutor idempotency, IWalletService wallet) =>
        {
            var userId = principal.GetUserId();
            return await IdempotencyHttp.RunAsync(http, idempotency, "POST /wallet/deposit", userId, req, async ct =>
            {
                var result = await wallet.DepositAsync(userId, req.Amount, ct);
                return IdempotentResponse.Of(StatusCodes.Status201Created, result);
            });
        });

        group.MapPost("/withdraw", async (
            AmountRequest req,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            HttpContext http, ClaimsPrincipal principal,
            IIdempotencyExecutor idempotency, IWalletService wallet) =>
        {
            var userId = principal.GetUserId();
            return await IdempotencyHttp.RunAsync(http, idempotency, "POST /wallet/withdraw", userId, req, async ct =>
            {
                var result = await wallet.WithdrawAsync(userId, req.Amount, ct);
                return IdempotentResponse.Of(StatusCodes.Status201Created, result);
            });
        });

        group.MapGet("/transactions", async (
            int? page, int? pageSize, ClaimsPrincipal principal, IWalletService wallet, CancellationToken ct)
            => Results.Ok(await wallet.GetTransactionsAsync(principal.GetUserId(), page ?? 1, pageSize ?? 20, ct)));
    }
}