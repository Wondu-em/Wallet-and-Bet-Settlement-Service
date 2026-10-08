namespace Wallet.IntegrationTests;

[CollectionDefinition("PostgreSQL integration", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
