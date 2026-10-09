using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Application.Audit;
using Wallet.Application.Auth;
using Wallet.Application.Outbox;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Services;

public sealed class AuthService(
    WalletDbContext db,
    IPasswordHasher hasher,
    IAuditWriter audit,
    IOutboxWriter outbox) : IAuthService
{
    public async Task<AuthenticatedUser> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        email = (email ?? "").Trim().ToLowerInvariant();

        var errors = new Dictionary<string, string[]>();
        if (!IsValidEmail(email)) errors["email"] = new[] { "A valid email address is required." };
        if (password is null || password.Length < 8 || password.Length > 128)
            errors["password"] = new[] { "Password must be between 8 and 128 characters." };
        if (errors.Count > 0) throw new AppValidationException(errors);

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("Email is already registered.");

        var user = new User { Email = email, PasswordHash = hasher.Hash(password!), Role = UserRole.User };
        db.Users.Add(user);
        db.Accounts.Add(new Account { Type = AccountType.UserWallet, OwnerUserId = user.Id });
        audit.Add(user.Id, "user.register", "user", user.Id.ToString(), new { email });
        outbox.Add("user.registered", new { userId = user.Id, email = user.Email, role = user.Role });

        try
        {
            await db.SaveChangesAsync(ct);   // user + wallet + audit in one implicit transaction
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("Email is already registered.");
        }

        return new AuthenticatedUser(user.Id, user.Email, user.Role);
    }

    public async Task<AuthenticatedUser> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);

        // Always run one hash verification so unknown emails take the same time as wrong passwords.
        var ok = hasher.Verify(password ?? "", user?.PasswordHash ?? hasher.DummyHash) && user is not null;
        if (!ok) throw new InvalidCredentialsException();

        audit.Add(user!.Id, "user.login", "user", user.Id.ToString());
        await db.SaveChangesAsync(ct);

        return new AuthenticatedUser(user.Id, user.Email, user.Role);
    }

    private static bool IsValidEmail(string email)
        => email.Length is > 3 and <= 320
           && MailAddress.TryCreate(email, out var parsed)
           && parsed.Address == email
           && parsed.Host.Contains('.');
}
