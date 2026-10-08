using Wallet.Domain;

namespace Wallet.Application.Auth;

public sealed record AuthenticatedUser(Guid Id, string Email, UserRole Role);

public sealed record TokenResult(string AccessToken, DateTimeOffset ExpiresAt);

public interface IAuthService
{
    /// <summary>Creates a User-role account and its ETB wallet atomically. Role is never client-controlled.</summary>
    Task<AuthenticatedUser> RegisterAsync(string email, string password, CancellationToken ct = default);

    Task<AuthenticatedUser> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);

    /// <summary>A valid hash of a random secret, used to keep login timing uniform for unknown emails.</summary>
    string DummyHash { get; }
}

public interface ITokenService
{
    TokenResult Create(AuthenticatedUser user);
}
