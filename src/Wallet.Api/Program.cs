using Microsoft.EntityFrameworkCore;
using Wallet.Infrastructure.Persistence;
using Wallet.Application.Ledger;
using Wallet.Infrastructure.Ledger;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<WalletDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
    
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<ITransactionRunner, TransactionRunner>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Apply migrations and seed system accounts on startup.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedSystemAccountsAsync(db);
}

app.MapGet("/health", async (WalletDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "healthy" })
        : Results.StatusCode(503));

app.Run();

public partial class Program;
