using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Wallet.Application.Auth;

namespace Wallet.Api.Security;

public sealed class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    public TokenResult Create(AuthenticatedUser user)
    {
        var o = options.Value;
        var expires = DateTime.UtcNow.AddMinutes(o.ExpiryMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", user.Id.ToString()),
                new Claim("email", user.Email),
                new Claim("role", user.Role.ToString()),
                new Claim("jti", Guid.NewGuid().ToString())
            }),
            Expires = expires,
            Issuer = o.Issuer,
            Audience = o.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256)
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new TokenResult(token, new DateTimeOffset(expires, TimeSpan.Zero));
    }
}
