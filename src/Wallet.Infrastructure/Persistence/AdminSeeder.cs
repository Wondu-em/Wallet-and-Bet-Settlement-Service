using Microsoft.EntityFrameworkCore;
using Wallet.Application.Auth;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence;

public static class AdminSeeder
{
    /// <summary>Creates the admin user from configuration (Admin:Email / Admin:Password) if it does not exist.</summary>
    public static async Task SeedAdminAsync(
        WalletDbContext db, IPasswordHasher hasher, string? email, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        email = email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;

        db.Users.Add(new User { Email = email, PasswordHash = hasher.Hash(password), Role = UserRole.Admin });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // another replica seeded it first
            db.ChangeTracker.Clear();
        }
    }
}
