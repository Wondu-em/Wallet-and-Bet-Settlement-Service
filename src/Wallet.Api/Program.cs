using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Wallet.Api.Endpoints;
using Wallet.Api.Security;
using Wallet.Api.Web;
using Wallet.Application.Audit;
using Wallet.Application.Auth;
using Wallet.Application.Bets;
using Wallet.Application.Events;
using Wallet.Application.Idempotency;
using Wallet.Application.Ledger;
using Wallet.Application.Wallets;
using Wallet.Infrastructure.Audit;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Ledger;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Security;
using Wallet.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------- Database & services ----------
builder.Services.AddDbContext<WalletDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<ITransactionRunner, TransactionRunner>();
builder.Services.AddScoped<IIdempotencyExecutor, IdempotencyExecutor>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<IBettingService, BettingService>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

// ---------- JWT ----------
var jwt = new JwtOptions
{
    Key = builder.Configuration["Jwt:Key"] ?? "",
    Issuer = builder.Configuration["Jwt:Issuer"] ?? "wallet-bet-api",
    Audience = builder.Configuration["Jwt:Audience"] ?? "wallet-bet-clients",
    ExpiryMinutes = int.TryParse(builder.Configuration["Jwt:ExpiryMinutes"], out var minutes) ? minutes : 60
};
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Configuration 'Jwt:Key' is required and must be at least 32 characters.");

builder.Services.AddSingleton<IOptions<JwtOptions>>(Options.Create(jwt));
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;   // keep "sub" and "role" claim names as issued
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("User", p => p.RequireRole("User"));
    o.AddPolicy("Admin", p => p.RequireRole("Admin"));
});

// ---------- Rate limiting (bet placement, per user) ----------
var betPermits = builder.Configuration.GetValue("RateLimit:Bets:PermitLimit", 10);
var betWindowSeconds = builder.Configuration.GetValue("RateLimit:Bets:WindowSeconds", 1);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    o.AddPolicy("bets", httpContext =>
    {
        var key = httpContext.User.FindFirst("sub")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = betPermits,
            Window = TimeSpan.FromSeconds(betWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    o.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers["Retry-After"] = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var problem = new ProblemDetails
        {
            Status = 429,
            Title = "Too many requests",
            Detail = "Bet placement rate limit exceeded. Retry shortly."
        };
        problem.Extensions["code"] = "rate_limited";
        await ctx.HttpContext.Response.WriteAsJsonAsync(problem, (JsonSerializerOptions?)null, "application/problem+json", ct);
    };
});

// ---------- HTTP plumbing ----------
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---------- Migrate & seed ----------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedSystemAccountsAsync(db);

    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    await AdminSeeder.SeedAdminAsync(db, hasher, app.Configuration["Admin:Email"], app.Configuration["Admin:Password"]);
}

// ---------- Endpoints ----------
app.MapGet("/health", async (WalletDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "healthy" })
        : Results.StatusCode(503)).AllowAnonymous();

app.MapAuthEndpoints();
app.MapWalletEndpoints();
app.MapEventEndpoints();
app.MapAdminEventEndpoints();
app.MapBetEndpoints();

app.Run();

public partial class Program;
