using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence;

public class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<BettingEvent> Events => Set<BettingEvent>();
    public DbSet<Outcome> Outcomes => Set<Outcome>();
    public DbSet<Bet> Bets => Set<Bet>();
    public DbSet<EventStatusHistory> EventHistory => Set<EventStatusHistory>();
    public DbSet<BetStatusHistory> BetHistory => Set<BetStatusHistory>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ProviderDeposit> ProviderDeposits => Set<ProviderDeposit>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Account>(e =>
        {
            e.ToTable("accounts", t => t.HasCheckConstraint(
                "ck_accounts_wallet_non_negative",
                "\"type\" <> 'UserWallet' OR balance >= 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            // exactly one wallet per user
            e.HasIndex(x => x.OwnerUserId).IsUnique().HasFilter("\"type\" = 'UserWallet'");
        });

        b.Entity<LedgerTransaction>(e =>
        {
            e.ToTable("ledger_transactions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ReferenceType).HasMaxLength(50).IsRequired();
            e.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries", t => t.HasCheckConstraint("ck_ledger_entries_amount_positive", "amount > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AccountId);
            e.HasIndex(x => x.TransactionId);
        });

        b.Entity<BettingEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Outcomes).WithOne().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.EscrowAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Outcome>().WithMany().HasForeignKey(x => x.WinningOutcomeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Status);
        });

        b.Entity<Outcome>(e =>
        {
            e.ToTable("outcomes", t => t.HasCheckConstraint("ck_outcomes_odds_gt_one", "odds_bp > 10000"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        });

        b.Entity<Bet>(e =>
        {
            e.ToTable("bets", t =>
            {
                t.HasCheckConstraint("ck_bets_stake_positive", "stake > 0");
                t.HasCheckConstraint("ck_bets_odds_gt_one", "odds_bp > 10000");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BettingEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Outcome>().WithMany().HasForeignKey(x => x.OutcomeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.PlacedAt });
            e.HasIndex(x => new { x.EventId, x.Status });
        });

        b.Entity<EventStatusHistory>(e =>
        {
            e.ToTable("event_status_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne<BettingEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.EventId);
        });

        b.Entity<BetStatusHistory>(e =>
        {
            e.ToTable("bet_status_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne<Bet>().WithMany().HasForeignKey(x => x.BetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.BetId);
        });

        b.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Scope).HasMaxLength(100).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(200).IsRequired();
            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.Scope, x.Endpoint, x.Key }).IsUnique();
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderEventId).HasMaxLength(200).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.RawPayload).IsRequired();
            e.HasIndex(x => x.ProviderEventId).IsUnique();
        });

        b.Entity<ProviderDeposit>(e =>
        {
            e.ToTable("provider_deposits", t => t.HasCheckConstraint("ck_provider_deposits_amount_positive", "amount > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderReference).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ProviderReference).IsUnique();
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.At);
        });

        // snake_case column names (OddsBp -> odds_bp)
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var p in entity.GetProperties())
                p.SetColumnName(ToSnakeCase(p.Name));
    }

    private static string ToSnakeCase(string name)
        => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
