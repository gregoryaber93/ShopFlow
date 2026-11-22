using System.Text.RegularExpressions;
using ShopFlow.Contracts.Analytics.V1;

namespace ShopFlow.Analytics.Application;

/// <summary>Event in the shape sent by the browser SDK. Every field is untrusted.</summary>
public sealed record ClickstreamEventInput(
    Guid? EventId,
    string? SessionId,
    string? EventType,
    string? Sku,
    string? PageUrl,
    string? Referrer,
    DateTimeOffset? OccurredAt,
    Dictionary<string, string>? Properties);

public sealed record ClickstreamBatchRequest(IReadOnlyList<ClickstreamEventInput>? Events);

public sealed record RejectedEvent(int Index, string Reason);

public sealed record IngestResult(int Accepted, IReadOnlyList<RejectedEvent> Rejected);

public interface IClickstreamPublisher
{
    /// <summary>Returns only after Event Hubs has accepted the events.</summary>
    Task PublishAsync(IReadOnlyList<ClickstreamEvent> events, CancellationToken cancellationToken);
}

/// <summary>
/// Accepts a batch of browser events: validates, sanitizes and forwards them to the <c>clickstream</c> hub.
/// </summary>
/// <remarks>
/// WHY: the endpoint is public (anonymous visitors generate events too), so everything in the request body is untrusted.
/// We reject individual bad events instead of the whole batch: the browser SDK does not retry sending, and one broken
/// event should not lose the rest.
/// </remarks>
public sealed partial class ClickstreamIngestor(IClickstreamPublisher publisher, TimeProvider timeProvider)
{
    public const int MaxEventsPerRequest = 100;

    private const int MaxProperties = 10;
    private const int MaxPropertyLength = 100;
    private const int MaxSkuLength = 64;

    /// <summary>Events older than this are rejected: the SDK sends every few seconds, and old data would land in closed windows anyway.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    /// <summary>Browser clocks are sometimes wrong; a time in the future is replaced with the time of receipt.</summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> AllowedTypes =
    [
        ClickstreamEventTypes.PageView,
        ClickstreamEventTypes.ProductView,
        ClickstreamEventTypes.AddToCart,
        ClickstreamEventTypes.RemoveFromCart,
        ClickstreamEventTypes.CheckoutStarted,
        ClickstreamEventTypes.OrderPlaced,
        ClickstreamEventTypes.Search,
    ];

    /// <param name="authenticatedCustomerId">
    /// Identifier from the token, passed by APIM in a header. Never from the request body.
    /// </param>
    public async Task<IngestResult> IngestAsync(ClickstreamBatchRequest request, Guid? authenticatedCustomerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputs = request.Events ?? [];
        if (inputs.Count > MaxEventsPerRequest)
        {
            throw new ArgumentException($"At most {MaxEventsPerRequest} events per request.", nameof(request));
        }

        var now = timeProvider.GetUtcNow();
        var accepted = new List<ClickstreamEvent>(inputs.Count);
        var rejected = new List<RejectedEvent>();

        for (var index = 0; index < inputs.Count; index++)
        {
            if (TryNormalize(inputs[index], authenticatedCustomerId, now, out var normalized, out var reason))
            {
                accepted.Add(normalized);
            }
            else
            {
                rejected.Add(new RejectedEvent(index, reason));
            }
        }

        if (accepted.Count > 0)
        {
            await publisher.PublishAsync(accepted, cancellationToken).ConfigureAwait(false);
        }

        AnalyticsTelemetry.RecordIngest(accepted.Count, rejected.Count);
        return new IngestResult(accepted.Count, rejected);
    }

    internal static bool TryNormalize(ClickstreamEventInput input, Guid? customerId, DateTimeOffset now, out ClickstreamEvent normalized, out string reason)
    {
        normalized = null!;

        if (input.EventId is not { } eventId || eventId == Guid.Empty)
        {
            reason = "event_id_missing";
            return false;
        }

        if (input.SessionId is null || !SessionIdPattern().IsMatch(input.SessionId))
        {
            reason = "session_id_invalid";
            return false;
        }

        if (input.EventType is null || !AllowedTypes.Contains(input.EventType))
        {
            reason = "event_type_unknown";
            return false;
        }

        if (input.OccurredAt is not { } occurredAt)
        {
            reason = "occurred_at_missing";
            return false;
        }

        if (now - occurredAt > MaxAge)
        {
            reason = "too_old";
            return false;
        }

        normalized = new ClickstreamEvent
        {
            EventId = eventId,
            SessionId = input.SessionId,

            // WHY: the CustomerId from the request body is ignored. Anyone can send any identifier and attribute their clicks to
            // someone else's account (or poison a customer's statistics). Only what APIM read from a validated token is trusted.
            CustomerId = customerId,
            EventType = input.EventType,
            Sku = Truncate(input.Sku, MaxSkuLength),
            PageUrl = SanitizeUrl(input.PageUrl, keepPathAndCampaign: true),
            Referrer = SanitizeUrl(input.Referrer, keepPathAndCampaign: false),
            OccurredAt = occurredAt - now > MaxClockSkew ? now : occurredAt,
            Properties = SanitizeProperties(input.Properties),
        };

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Keeps only the parts of the URL that analytics needs.
    /// </summary>
    /// <remarks>
    /// WHY: query parameters in URLs are a common source of leaked personal data and secrets: an e-mail address in a
    /// newsletter link, a password reset token, a payment session identifier. The clickstream goes to the archive (Capture)
    /// for years, so we keep only campaign parameters (<c>utm_*</c>). Only the domain is kept from the referrer.
    /// </remarks>
    internal static string? SanitizeUrl(string? value, bool keepPathAndCampaign)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        if (!keepPathAndCampaign)
        {
            return uri.Host;
        }

        var campaign = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => pair.StartsWith("utm_", StringComparison.OrdinalIgnoreCase));

        var query = string.Join('&', campaign);
        var cleaned = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}{(query.Length > 0 ? "?" + query : string.Empty)}";
        return Truncate(cleaned, 512);
    }

    private static Dictionary<string, string>? SanitizeProperties(Dictionary<string, string>? properties) =>
        properties is null or { Count: 0 }
            ? null
            : properties
                .Take(MaxProperties)
                .ToDictionary(pair => Truncate(pair.Key, MaxPropertyLength)!, pair => Truncate(pair.Value, MaxPropertyLength) ?? string.Empty);

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SessionIdPattern();
}
