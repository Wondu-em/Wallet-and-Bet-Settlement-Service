using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wallet.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` so tooling never has to boot the whole API.</summary>
public class WalletDbContextFactory : IDesignTimeDbContextFactory<WalletDbContext>
{
    public WalletDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                 ?? "Host=localhost;Port=5432;Database=walletdb;Username=wallet;Password=wallet_dev_pw";
        var options = new DbContextOptionsBuilder<WalletDbContext>().UseNpgsql(cs).Options;
        return new WalletDbContext(options);
    }
}
