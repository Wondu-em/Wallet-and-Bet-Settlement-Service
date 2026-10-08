using Wallet.Application.Idempotency;
using Wallet.Application.Ledger;
using Wallet.Domain;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Ledger;
using Xunit;

namespace Wallet.IntegrationTests;

public class IdempotencyExecutorTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private const string Endpoint = "POST /wallet/deposit";

    private async Task<IdempotentResponse> RunDepositAsync(Guid wallet, string key, string hash, int[] counter, long amount = 1_000)
    {
        await using var db = fx.CreateContext();               // one context per "request"
        var executor = new IdempotencyExecutor(db);
        var ledger = new LedgerService(db);

        return await executor.ExecuteAsync(new IdempotencyRequest("user:test", Endpoint, key, hash), async ct =>
        {
            Interlocked.Increment(ref counter[0]);
            await ledger.PostAsync(Postings.Deposit(wallet, amount), ct);
            return IdempotentResponse.Of(201, new { ok = true });
        });
    }

    [Fact]
    public async Task Sequential_duplicate_replays_the_stored_response()
    {
        var wallet = await fx.CreateWalletAsync();
        var key = Guid.NewGuid().ToString();
        var counter = new int[1];

        var first = await RunDepositAsync(wallet, key, "h1", counter);
        var second = await RunDepositAsync(wallet, key, "h1", counter);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(1, counter[0]);

        var (cached, derived) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(1_000, cached);
        Assert.Equal(1_000, derived);
    }

    [Fact]
    public async Task Concurrent_duplicates_apply_the_effect_exactly_once()
    {
        var wallet = await fx.CreateWalletAsync();
        var key = Guid.NewGuid().ToString();
        var counter = new int[1];

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => RunDepositAsync(wallet, key, "h1", counter)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, counter[0]);                              // business logic ran once
        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(9, results.Count(r => r.Replayed));
        Assert.All(results, r => Assert.Equal(201, r.StatusCode));

        var (cached, derived) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(1_000, cached);                              // credited once, not ten times
        Assert.Equal(1_000, derived);
        await fx.AssertLedgerGloballyBalancedAsync();
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_rejected()
    {
        var wallet = await fx.CreateWalletAsync();
        var key = Guid.NewGuid().ToString();
        var counter = new int[1];

        await RunDepositAsync(wallet, key, "hash-A", counter);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            RunDepositAsync(wallet, key, "hash-B", counter, amount: 9_999));

        Assert.Equal(1, counter[0]);
        var (cached, _) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(1_000, cached);
    }

    [Fact]
    public async Task Failed_request_rolls_back_and_the_key_can_be_retried()
    {
        var wallet = await fx.CreateWalletAsync();
        var key = Guid.NewGuid().ToString();

        await using (var db = fx.CreateContext())
        {
            var executor = new IdempotencyExecutor(db);
            var ledger = new LedgerService(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync(new IdempotencyRequest("user:test", Endpoint, key, "h1"), async ct =>
                {
                    await ledger.PostAsync(Postings.Deposit(wallet, 1_000), ct);   // written, then...
                    throw new InvalidOperationException("boom");                     // ...everything rolls back
                }));
        }

        var (afterFailure, _) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(0, afterFailure);                                              // no partial effect

        var counter = new int[1];
        var retry = await RunDepositAsync(wallet, key, "h1", counter);              // same key works again
        Assert.False(retry.Replayed);
        Assert.Equal(1, counter[0]);
        var (afterRetry, _) = await fx.GetBalancesAsync(wallet);
        Assert.Equal(1_000, afterRetry);
    }

    [Fact]
    public async Task Non_success_responses_are_not_cached()
    {
        var key = Guid.NewGuid().ToString();
        var executions = 0;

        for (var i = 0; i < 2; i++)
        {
            await using var db = fx.CreateContext();
            var executor = new IdempotencyExecutor(db);
            var response = await executor.ExecuteAsync(new IdempotencyRequest("user:test", Endpoint, key, "h1"), _ =>
            {
                executions++;
                return Task.FromResult(IdempotentResponse.Of(422, new { error = "nope" }));
            });
            Assert.Equal(422, response.StatusCode);
            Assert.False(response.Replayed);
        }

        Assert.Equal(2, executions);   // executed twice: failures are retried, not replayed
    }
}
