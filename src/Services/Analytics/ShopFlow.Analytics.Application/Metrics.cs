using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using ShopFlow.Contracts.Analytics.V1;

namespace ShopFlow.Analytics.Application;

/// <summary>A one-minute metric window. The identifier is also the partition key of the Cosmos DB document.</summary>
public sealed record MetricWindow(string Metric, DateTimeOffset Start)
{
    public static readonly TimeSpan Size = TimeSpan.FromMinutes(1);

    public string Id => string.Create(CultureInfo.InvariantCulture, $"{Metric}:{Start.UtcDateTime:yyyy-MM-ddTHH:mm}");

    public static MetricWindow For(string metric, DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new MetricWindow(metric, new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero));
    }
}

/// <summary>Counter increments for one window. Keys are flat, e.g. <c>revenueMinor.PLN</c>.</summary>
public sealed record MetricIncrement(MetricWindow Window, IReadOnlyDictionary<string, long> Counters);

public sealed record MetricWindowView(DateTimeOffset Start, IReadOnlyDictionary<string, long> Counters);

public interface IMetricStore
{
    /// <summary>"At least once" increment: when a batch is repeated the counters grow a second time (acceptable for clickstream).</summary>
    Task ApplyAsync(MetricIncrement increment, CancellationToken cancellationToken);

    /// <summary>Exactly-once increment for a given event. Returns <c>false</c> when the event was already counted.</summary>
    Task<bool> ApplyOnceAsync(Guid eventId, MetricIncrement increment, CancellationToken cancellationToken);

    Task<IReadOnlyList<MetricWindowView>> ReadAsync(string metric, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public static class MetricNames
{
    public const string Funnel = "funnel";
    public const string Business = "business";
}

/// <summary>
/// Conversion funnel from the clickstream: the number of events of each type per minute.
/// </summary>
/// <remarks>
/// WHY: pre-aggregation in the batch's memory. A batch from Event Hubs has hundreds of events, and most of them fall into
/// one window. Instead of hundreds of writes to one document (a hot logical partition, 429s with a few instances) we do one
/// write per window per batch. The number of writes grows with the number of batches, not events.
/// </remarks>
public static class FunnelAggregator
{
    /// <summary>How much skew we tolerate in the browser's event time; beyond that we count by the time of receipt.</summary>
    public static readonly TimeSpan MaxEventTimeDrift = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<MetricIncrement> Aggregate(IEnumerable<(ClickstreamEvent Event, DateTimeOffset EnqueuedAt)> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .GroupBy(item => MetricWindow.For(MetricNames.Funnel, EventTime(item.Event.OccurredAt, item.EnqueuedAt)))
            .Select(window => new MetricIncrement(
                window.Key,
                window
                    .GroupBy(item => item.Event.EventType)
                    .ToDictionary(type => type.Key, type => (long)type.Count())
                    .Append(new KeyValuePair<string, long>("events", window.Count()))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)))
            .ToList();
    }

    /// <remarks>
    /// WHY: windows are computed by event time (when the customer clicked), not processing time. After an outage and while
    /// catching up, processing time would put an hour of traffic into one minute. We trust the browser's time only within
    /// reason relative to the time Event Hubs received the event.
    /// </remarks>
    public static DateTimeOffset EventTime(DateTimeOffset occurredAt, DateTimeOffset enqueuedAt) =>
        (occurredAt - enqueuedAt).Duration() <= MaxEventTimeDrift ? occurredAt : enqueuedAt;
}

/// <summary>
/// Business metrics from the <c>business-events</c> hub: orders, revenue, refunds, rejection reasons per minute.
/// </summary>
public static class BusinessMetricsCalculator
{
    public static MetricIncrement? Calculate(BusinessEvent businessEvent)
    {
        ArgumentNullException.ThrowIfNull(businessEvent);

        var counters = (businessEvent.EventType, businessEvent.ContractVersion) switch
        {
            // WHY: version 2 only. During the sunset period Orders publishes OrderSubmitted in both versions, so counting both
            // would show twice as many orders.
            ("OrderSubmitted", 2) => Single("ordersSubmitted"),
            ("OrderSubmitted", _) => null,
            ("OrderConfirmed", _) => Single("ordersConfirmed"),
            ("OrderCancelled", _) => Single("ordersCancelled"),
            ("OrderRejected", _) => new Dictionary<string, long>
            {
                ["ordersRejected"] = 1,
                [$"rejections.{ReadString(businessEvent.Payload, "reasonCode") ?? "unknown"}"] = 1,
            },
            ("PaymentCaptured", _) => Money(businessEvent.Payload, "revenueMinor"),
            ("PaymentRefunded", _) => Money(businessEvent.Payload, "refundsMinor"),
            _ => null,
        };

        return counters is null ? null : new MetricIncrement(MetricWindow.For(MetricNames.Business, businessEvent.OccurredAt), counters);
    }

    /// <summary>Amount in minor units (grosze, cents).</summary>
    /// <remarks>
    /// AZURE: the Increment operation in Cosmos DB works on integers and floating-point numbers (double). Summing amounts as
    /// double loses cents (0.1 + 0.2 ≠ 0.3), so we store integers in minor units. The shop sells in currencies with two
    /// decimal places; a currency with a different number of places (JPY, KWD) would need an ISO 4217 exponent table.
    /// </remarks>
    public static long ToMinorUnits(decimal amount) => (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static Dictionary<string, long> Single(string counter) => new() { [counter] = 1 };

    private static Dictionary<string, long>? Money(JsonElement payload, string counter)
    {
        if (!payload.TryGetProperty("amount", out var money)
            || !money.TryGetProperty("amount", out var amountElement)
            || !amountElement.TryGetDecimal(out var amount)
            || ReadString(money, "currency") is not { } currency
            || amount <= 0m)
        {
            // A refund of 0 is a released hold, not a money movement.
            return null;
        }

        return new Dictionary<string, long> { [$"{counter}.{currency}"] = ToMinorUnits(amount) };
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Analytics process metrics in Application Insights.</summary>
public static class AnalyticsTelemetry
{
    public const string MeterName = "ShopFlow.Analytics";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> IngestedCounter =
        Meter.CreateCounter<long>("shopflow.analytics.clickstream.ingested", "{event}", "Clickstream events accepted or rejected at ingest.");

    private static readonly Counter<long> ForwardedCounter =
        Meter.CreateCounter<long>("shopflow.analytics.business_events.forwarded", "{event}", "Integration events forwarded from Service Bus to Event Hubs.");

    private static readonly Counter<long> SkippedCounter =
        Meter.CreateCounter<long>("shopflow.analytics.events.skipped", "{event}", "Events that could not be parsed by an aggregator.");

    public static void RecordIngest(int accepted, int rejected)
    {
        IngestedCounter.Add(accepted, new KeyValuePair<string, object?>("outcome", "accepted"));
        IngestedCounter.Add(rejected, new KeyValuePair<string, object?>("outcome", "rejected"));
    }

    public static void RecordForwarded(int count) => ForwardedCounter.Add(count);

    public static void RecordSkipped(string hub, string reason) =>
        SkippedCounter.Add(1, new KeyValuePair<string, object?>("hub", hub), new KeyValuePair<string, object?>("reason", reason));
}
