using Wallet.Application.Auth;

namespace Wallet.Api.Endpoints;

public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/register", async (RegisterRequest req, IAuthService auth, CancellationToken ct) =>
        {
            var user = await auth.RegisterAsync(req.Email, req.Password, ct);
            return Results.Created($"/users/{user.Id}", new { userId = user.Id, email = user.Email });
        });

        group.MapPost("/login", async (LoginRequest req, IAuthService auth, ITokenService tokens, CancellationToken ct) =>
        {
            var user = await auth.ValidateCredentialsAsync(req.Email, req.Password, ct);
            var token = tokens.Create(user);
            return Results.Ok(new { accessToken = token.AccessToken, tokenType = "Bearer", expiresAt = token.ExpiresAt });
        });
    }
}
