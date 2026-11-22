using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopFlow.Payments.Application.Webhooks;

namespace ShopFlow.Payments.Infrastructure.Psp;

/// <summary>
/// Webhook signature verification: <c>Psp-Signature: t=1763725200,v1=&lt;hex HMAC-SHA256("{t}.{body}")&gt;</c>.
/// </summary>
/// <remarks>
/// WHY: the webhook endpoint is public (the provider calls it from the internet through API Management), so
/// authentication is a signature of the body with a shared secret, not an Entra ID token. Three safeguards:
/// <list type="bullet">
/// <item>HMAC of the whole raw body: changing a single byte invalidates the signature. That is why we verify the bytes
/// before deserialization, not the object after it.</item>
/// <item>A timestamp in the signature and a 5-minute tolerance: an intercepted request cannot be replayed later.</item>
/// <item>Constant-time comparison: the response time does not reveal how many bytes of the signature were correct.</item>
/// </list>
/// APIM additionally restricts the endpoint to the provider's IP addresses and applies a rate limit.
/// </remarks>
public sealed class PspWebhookVerifier(IOptionsMonitor<PspOptions> options, TimeProvider timeProvider)
{
    public const string SignatureHeader = "Psp-Signature";

    public bool IsValid(string? signatureHeader, ReadOnlySpan<byte> body)
    {
        if (!TryParse(signatureHeader, out var timestamp, out var signature))
        {
            return false;
        }

        var settings = options.CurrentValue;
        var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age.Duration() > settings.WebhookTolerance)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}."));
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));

        foreach (var secret in settings.WebhookSecrets)
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
            if (CryptographicOperations.FixedTimeEquals(expected, signature))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(string? header, out long timestamp, out byte[] signature)
    {
        timestamp = 0;
        signature = [];

        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("t=", StringComparison.Ordinal))
            {
                _ = long.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out timestamp);
            }
            else if (part.StartsWith("v1=", StringComparison.Ordinal))
            {
                try
                {
                    signature = Convert.FromHexString(part.AsSpan(3));
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }

        return timestamp > 0 && signature.Length == SHA256.HashSizeInBytes;
    }
}

/// <summary>Provider webhook body → <see cref="PspWebhookEvent"/>.</summary>
public static class PspWebhookParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <returns>The event, or <c>null</c> when the body lacks required fields.</returns>
    public static PspWebhookEvent? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<WebhookPayload>(body, Json);
            if (payload?.Data?.Metadata is not { } metadata || string.IsNullOrEmpty(payload.Id))
            {
                return null;
            }

            return new PspWebhookEvent(
                payload.Id,
                payload.Type,
                metadata.OrderId,
                metadata.PaymentId,
                payload.Data.AuthorizationId,
                payload.Data.DeclineCode,
                payload.Data.ExpiresAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record WebhookPayload(string Id, string Type, WebhookData? Data);

    private sealed record WebhookData(string AuthorizationId, string? DeclineCode, DateTimeOffset? ExpiresAt, WebhookMetadata? Metadata);

    private sealed record WebhookMetadata(Guid OrderId, Guid PaymentId);
}
