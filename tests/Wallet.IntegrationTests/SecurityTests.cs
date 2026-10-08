using Wallet.Infrastructure.Security;
using Xunit;

namespace Wallet.IntegrationTests;

public class SecurityTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Correct_password_verifies_and_wrong_password_does_not()
    {
        var hash = _hasher.Hash("Passw0rd!123");
        Assert.True(_hasher.Verify("Passw0rd!123", hash));
        Assert.False(_hasher.Verify("wrong-password", hash));
    }

    [Fact]
    public void Same_password_hashes_differently_each_time()
        => Assert.NotEqual(_hasher.Hash("Passw0rd!123"), _hasher.Hash("Passw0rd!123"));

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("v1.abc.def.ghi")]
    [InlineData("v2.1000.AAAA.BBBB")]
    public void Malformed_hash_never_verifies(string stored)
        => Assert.False(_hasher.Verify("anything", stored));
}
