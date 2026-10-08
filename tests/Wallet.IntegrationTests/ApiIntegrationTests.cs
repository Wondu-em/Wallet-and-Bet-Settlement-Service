using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wallet.Domain;

namespace Wallet.IntegrationTests;

[Collection("PostgreSQL integration")]
public sealed class ApiIntegrationTests : IDisposable
{
    private const string JwtKey = "integration-test-jwt-key-at-least-32-characters";
    private const string WebhookSecret = "integration-test-webhook-secret-at-least-32-bytes";
    private const string AdminEmail = "integration-admin@example.test";
    private const string AdminPassword = "IntegrationAdmin123!";

    private readonly PostgresFixture _fx;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiIntegrationTests(PostgresFixture fx)
    {
        _fx = fx;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = fx.ConnectionString,
                    ["Jwt:Key"] = JwtKey,
                    ["Jwt:Issuer"] = "wallet-bet-api",
                    ["Jwt:Audience"] = "wallet-bet-clients",
                    ["Webhook:Secret"] = WebhookSecret,
                    ["Admin:Email"] = AdminEmail,
                    ["Admin:Password"] = AdminPassword
                })));
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Signed_webhook_endpoint_credits_once_and_rejects_invalid_signatures()
    {
        var (userId, walletId) = await TestData.CreateUserAsync(_fx);
        var eventId = Guid.NewGuid().ToString("N");
        var requestBody = JsonSerializer.Serialize(new
        {
            providerEventId = eventId,
            providerReference = Guid.NewGuid().ToString("N"),
            userId,
            amount = 3_400,
            status = "Confirmed"
        });
        var key = Guid.NewGuid().ToString("N");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Sign(WebhookSecret, timestamp, requestBody);

        using var first = await SendWebhookAsync(requestBody, key, signature, timestamp);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Confirmed", firstResult.GetProperty("status").GetString());
        Assert.False(firstResult.GetProperty("duplicate").GetBoolean());

        using var replay = await SendWebhookAsync(requestBody, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());

        using var invalidSignature = await SendWebhookAsync(requestBody, Guid.NewGuid().ToString("N"), "00");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidSignature.StatusCode);
        var staleTimestamp = (DateTimeOffset.UtcNow - TimeSpan.FromMinutes(6)).ToUnixTimeSeconds().ToString();
        using var staleRequest = await SendWebhookAsync(requestBody, Guid.NewGuid().ToString("N"),
            Sign(WebhookSecret, staleTimestamp, requestBody), staleTimestamp);
        Assert.Equal(HttpStatusCode.Unauthorized, staleRequest.StatusCode);

        var (cached, derived) = await _fx.GetBalancesAsync(walletId);
        Assert.Equal(3_400, cached);
        Assert.Equal(3_400, derived);
        await _fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Api_enforces_roles_and_users_only_see_their_own_bets()
    {
        using var anonymousRequest = await _client.PostAsJsonAsync("/admin/events", new
        {
            title = "Anonymous event",
            outcomes = new[] { new { name = "Home", odds = 2.0m }, new { name = "Away", odds = 2.0m } }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRequest.StatusCode);

        var firstUser = await RegisterAndLoginAsync("first-integration-user@example.test");
        var secondUser = await RegisterAndLoginAsync("second-integration-user@example.test");
        using (var depositRequest = new HttpRequestMessage(HttpMethod.Post, "/wallet/deposit")
        {
            Content = JsonContent.Create(new { amount = 1_000 })
        })
        {
            depositRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstUser.Token);
            depositRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var depositResponse = await _client.SendAsync(depositRequest);
            Assert.Equal(HttpStatusCode.Created, depositResponse.StatusCode);
        }

        var ev = await TestData.CreateEventAsync(_fx, 2.0m, 3.0m);
        await using (var db = _fx.CreateContext())
        {
            await TestData.Betting(db).PlaceBetAsync(firstUser.UserId, ev.Outcomes[0].Id, 100);
        }

        using var firstBetsRequest = new HttpRequestMessage(HttpMethod.Get, "/bets");
        firstBetsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstUser.Token);
        using var firstBetsResponse = await _client.SendAsync(firstBetsRequest);
        Assert.Equal(HttpStatusCode.OK, firstBetsResponse.StatusCode);
        var firstBets = await firstBetsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, firstBets.GetProperty("total").GetInt32());

        using var secondBetsRequest = new HttpRequestMessage(HttpMethod.Get, "/bets");
        secondBetsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secondUser.Token);
        using var secondBetsResponse = await _client.SendAsync(secondBetsRequest);
        Assert.Equal(HttpStatusCode.OK, secondBetsResponse.StatusCode);
        var secondBets = await secondBetsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBets.GetProperty("total").GetInt32());

        using var forbiddenAdminRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/events")
        {
            Content = JsonContent.Create(new
            {
                title = "User-created event",
                outcomes = new[] { new { name = "Home", odds = 2.0m }, new { name = "Away", odds = 2.0m } }
            })
        };
        forbiddenAdminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstUser.Token);
        using var forbiddenResponse = await _client.SendAsync(forbiddenAdminRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        var adminToken = await LoginAsync(AdminEmail, AdminPassword);
        using var authorizedAdminRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/events")
        {
            Content = JsonContent.Create(new
            {
                title = "Admin-created event",
                outcomes = new[] { new { name = "Home", odds = 2.0m }, new { name = "Away", odds = 2.0m } }
            })
        };
        authorizedAdminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var authorizedResponse = await _client.SendAsync(authorizedAdminRequest);
        Assert.Equal(HttpStatusCode.Created, authorizedResponse.StatusCode);
    }

    [Fact]
    public async Task Swagger_documents_auth_and_required_request_headers_per_operation()
    {
        using var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");

        var login = paths.GetProperty("/auth/login").GetProperty("post");
        Assert.False(login.TryGetProperty("security", out _));

        var deposit = paths.GetProperty("/wallet/deposit").GetProperty("post");
        Assert.True(deposit.GetProperty("security").GetArrayLength() > 0);
        var depositParameters = deposit.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(depositParameters,
            parameter => parameter.TryGetProperty("name", out var name) &&
                         name.GetString() == "Idempotency-Key" &&
                         parameter.TryGetProperty("required", out var required) &&
                         required.GetBoolean());

        var webhook = paths.GetProperty("/webhooks/deposit").GetProperty("post");
        Assert.False(webhook.TryGetProperty("security", out _));
        var webhookHeaders = webhook.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.TryGetProperty("name", out _))
            .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
        Assert.Contains("Idempotency-Key", webhookHeaders);
        Assert.Contains("X-Timestamp", webhookHeaders);
        Assert.Contains("X-Signature", webhookHeaders);
    }

    private async Task<(Guid UserId, string Token)> RegisterAndLoginAsync(string email)
    {
        using var register = await _client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "IntegrationUser123!"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var registration = await register.Content.ReadFromJsonAsync<JsonElement>();
        return (registration.GetProperty("userId").GetGuid(), await LoginAsync(email, "IntegrationUser123!"));
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client.PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("Login response did not contain an access token.");
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(
        string body,
        string idempotencyKey,
        string? signatureOverride = null,
        string? timestampOverride = null)
    {
        var timestamp = timestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = signatureOverride ?? Sign(WebhookSecret, timestamp, body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/deposit")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Signature", signature);
        return await _client.SendAsync(request);
    }

    private static string Sign(string secret, string timestamp, string body)
        => Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
