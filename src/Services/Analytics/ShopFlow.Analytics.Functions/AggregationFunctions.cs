using System.Text.Json;
using Azure.Messaging.EventHubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Analytics.Application;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Analytics.V1;
using ShopFlow.Functions;

namespace ShopFlow.Analytics.Functions;

/// <summary>
/// Hot path: one-minute aggregates computed live from Event Hubs. The cold path is Capture to ADLS Gen2 (hub
/// configuration in Bicep), from which everything can be recomputed.
/// </summary>
/// <remarks>
/// AZURE: after an exception in a function with an Event Hubs trigger the checkpoint moves on anyway, so the batch is lost.
/// The broker has no DLQ or redelivery here like Service Bus. A function-level retry policy (<c>ExponentialBackoffRetry</c>)
/// holds the checkpoint until the attempts finish, so a brief 429 from Cosmos DB does not lose data. The number of
/// attempts is finite: retrying forever would block the partition forever. After the attempts are exhausted the batch is
/// lost for the hot path, but it stays in the Capture archive.
/// <para>
/// AZURE: every consumer group has its own checkpoints (in a container of the AzureWebJobsStorage account), so the funnel
/// and the business metrics read the same streams independently. A new consumer is a new group in Bicep, with no impact
/// on existing ones.
/// </para>
/// </remarks>
public sealed class AggregationFunctions(IMetricStore metrics)
{
    private static readonly JsonSerializerOptions Json = ContractJson.Options;

    /// <remarks>
    /// WHY: funnel counters are approximate. A repeated batch (retry, a partition taken over by another instance) increases
    /// them a second time. For a conversion trend this does not matter, and exact deduplication at thousands of clicks per
    /// second would cost many times more RU than the counters themselves.
    /// </remarks>
    [Function("funnel-aggregator")]
    [ExponentialBackoffRetry(5, "00:00:02", "00:01:00")]
    public async Task Funnel(
        [EventHubTrigger(MessagingTopology.EventHubs.Clickstream, Connection = "EventHubs", ConsumerGroup = MessagingTopology.EventHubs.ConsumerGroups.FunnelAggregator, IsBatched = true)]
        EventData[] events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var parsed = new List<(ClickstreamEvent Event, DateTimeOffset EnqueuedAt)>(events.Length);
        foreach (var eventData in events)
        {
            // WHY: an event that cannot be read is skipped (with a metric) rather than throwing. An exception would retry the whole
            // batch, and a broken event will not become valid on the next attempt.
            if (TryRead<ClickstreamEvent>(eventData, MessagingTopology.EventHubs.Clickstream) is { } clickstreamEvent)
            {
                parsed.Add((clickstreamEvent, eventData.EnqueuedTime));
            }
        }

        foreach (var increment in FunnelAggregator.Aggregate(parsed))
        {
            await metrics.ApplyAsync(increment, cancellationToken);
        }
    }

    /// <remarks>
    /// WHY: here the number of events is small (orders, payments) and the numbers are money. Every event is counted exactly
    /// once thanks to a marker saved in the same transaction as the counter increment, so duplicates from the forwarder and
    /// repeated batches do not inflate revenue.
    /// </remarks>
    [Function("business-metrics")]
    [ExponentialBackoffRetry(5, "00:00:02", "00:01:00")]
    public async Task Business(
        [EventHubTrigger(MessagingTopology.EventHubs.BusinessEvents, Connection = "EventHubs", ConsumerGroup = MessagingTopology.EventHubs.ConsumerGroups.BusinessMetrics, IsBatched = true)]
        EventData[] events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var eventData in events)
        {
            if (TryRead<BusinessEvent>(eventData, MessagingTopology.EventHubs.BusinessEvents) is not { } businessEvent
                || BusinessMetricsCalculator.Calculate(businessEvent) is not { } increment)
            {
                continue;
            }

            await metrics.ApplyOnceAsync(businessEvent.EventId, increment, cancellationToken);
        }
    }

    private static T? TryRead<T>(EventData eventData, string hub)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(eventData.EventBody.ToMemory().Span, Json);
        }
        catch (JsonException)
        {
            AnalyticsTelemetry.RecordSkipped(hub, "invalid_json");
            return null;
        }
    }
}

/// <summary>Reading aggregates for the back-office dashboard.</summary>
public sealed class MetricsQueryFunction(IMetricStore metrics, TimeProvider timeProvider)
{
    public const string ReaderRole = "Analytics.Reader";

    [Function("metrics-query")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/{metric}")] HttpRequest request,
        string metric,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.IsInRole(ReaderRole) != true)
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        if (metric is not (MetricNames.Funnel or MetricNames.Business))
        {
            return new NotFoundResult();
        }

        var now = timeProvider.GetUtcNow();
        var to = DateTimeOffset.TryParse(request.Query["to"], out var parsedTo) ? parsedTo : now;
        var from = DateTimeOffset.TryParse(request.Query["from"], out var parsedFrom) ? parsedFrom : to.AddHours(-1);
        if (from > to)
        {
            return new BadRequestObjectResult(new { error = "from_after_to" });
        }

        return new OkObjectResult(await metrics.ReadAsync(metric, from, to, cancellationToken));
    }
}
