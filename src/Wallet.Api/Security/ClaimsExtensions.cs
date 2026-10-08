using System.Security.Claims;

namespace Wallet.Api.Security;

public static class ClaimsExtensions
{
    /// <summary>The authenticated user's id, always taken from the token (never from a request body).</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("Missing user id claim.");
    }
}
