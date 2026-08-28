using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations.Zoom;

/// <summary>
/// Zoom's documented webhook authentication scheme: the app's Feature &gt;
/// Event Subscriptions "Secret Token" HMAC-SHA256-signs
/// <c>v0:{x-zm-request-timestamp}:{raw request body}</c>; the result (hex,
/// lower-case) is compared against the <c>x-zm-signature</c> header
/// (prefixed "v0="). The same secret also answers Zoom's one-time
/// "endpoint.url_validation" challenge when the webhook URL is first
/// configured in the Marketplace/App dashboard.
/// </summary>
public static class ZoomWebhookValidator
{
    public static bool IsValidSignature(string secretToken, string timestamp, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        var expected = "v0=" + ComputeHmacHex(secretToken, $"v0:{timestamp}:{rawBody}");
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(signatureHeader);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public static string ComputeUrlValidationHash(string secretToken, string plainToken) =>
        ComputeHmacHex(secretToken, plainToken);

    /// <summary>Rejects a stale or clock-skewed/replayed timestamp. Zoom
    /// sends this as Unix milliseconds.</summary>
    public static bool IsFreshTimestamp(string timestamp, Instant now, Duration tolerance)
    {
        if (!long.TryParse(timestamp, out var millis))
        {
            return false;
        }

        var eventInstant = Instant.FromUnixTimeMilliseconds(millis);
        var delta = now - eventInstant;
        if (delta < Duration.Zero)
        {
            delta = -delta;
        }

        return delta <= tolerance;
    }

    private static string ComputeHmacHex(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
