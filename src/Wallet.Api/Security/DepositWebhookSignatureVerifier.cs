using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wallet.Api.Security;

public sealed class DepositWebhookSignatureVerifier
{
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);
    private readonly byte[] _secret;

    public DepositWebhookSignatureVerifier(string secret)
    {
        _secret = Encoding.UTF8.GetBytes(secret);
        if (_secret.Length < 32)
            throw new InvalidOperationException(
                "Configuration 'Webhook:Secret' is required and must be at least 32 bytes.");
    }

    public bool IsValid(ReadOnlySpan<byte> rawBody, string? timestamp, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
            return false;

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (signedAt < now - AllowedClockSkew || signedAt > now + AllowedClockSkew)
            return false;
        if (signature.Length != 64)
            return false;

        byte[] providedSignature;
        try
        {
            providedSignature = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var timestampBytes = Encoding.UTF8.GetBytes(timestamp);
        var signedContent = new byte[timestampBytes.Length + 1 + rawBody.Length];
        timestampBytes.CopyTo(signedContent, 0);
        signedContent[timestampBytes.Length] = (byte)'.';
        rawBody.CopyTo(signedContent.AsSpan(timestampBytes.Length + 1));

        var expectedSignature = HMACSHA256.HashData(_secret, signedContent);
        return CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature);
    }
}
