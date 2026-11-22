using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using ShopFlow.Analytics.Application;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;

namespace ShopFlow.Analytics.Infrastructure;

public static class AnalyticsContainers
{
    public const string DatabaseName = "analytics";

    /// <summary>
    /// Metric windows partitioned by <c>/metricId</c> (<c>{metric}:{minute}</c>).
    /// </summary>
    /// <remarks>
    /// AZURE: every minute is a separate logical partition, so writes spread over time, and reading a range is a
    /// <c>ReadMany</c> over known identifiers (point reads), not a query across all partitions.
    /// The downside is that within one minute all writes hit one partition; that is why the aggregators sum increments in
    /// the batch's memory before writing.
    /// </remarks>
    public const string AggregatesName = "aggregates";

    public const string PartitionKey = "metricId";
}

internal sealed class MetricWindowDocument : CosmosDocument
{
    public const string DocumentType = "metric-window";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    [JsonPropertyName(AnalyticsContainers.PartitionKey)]
    public required string MetricId { get; init; }

    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("windowStart")]
    public required DateTimeOffset WindowStart { get; init; }

    [JsonPropertyName("counters")]
    public Dictionary<string, long> Counters { get; init; } = [];
}

/// <summary>Marker of a counted event, in the partition of the window it fell into.</summary>
internal sealed class CountedEventDocument : CosmosDocument
{
    public const string DocumentType = "counted-event";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    [JsonPropertyName(AnalyticsContainers.PartitionKey)]
    public required string MetricId { get; init; }
}

/// <summary>
/// Window counters in Cosmos DB through partial document update (Patch) with the Increment operation.
/// </summary>
/// <remarks>
/// AZURE: Increment runs on the server and is atomic. Several function instances (one per Event Hubs partition) can
/// increase the same counter at the same time without a read, an ETag or retries after a conflict, which with a
/// "read, modify, write" pattern would end in an avalanche of 412s. Increment creates a missing field with the given
/// value, so new event types do not require changing the document.
/// </remarks>
internal sealed class CosmosMetricStore(Database database) : IMetricStore
{
    /// <summary>Cosmos DB accepts at most 10 operations in one Patch request.</summary>
    internal const int MaxPatchOperations = 10;

    /// <summary>The longest read range (in minutes), so that one dashboard view does not read thousands of documents.</summary>
    internal const int MaxWindowsPerRead = 240;

    private static readonly TimeSpan WindowTimeToLive = TimeSpan.FromDays(90);

    /// <summary>
    /// How long a counted event is remembered.
    /// </summary>
    /// <remarks>
    /// A duplicate from the forwarder arrives within minutes (batch retry). The hub's retention is 3 days, so beyond that the
    /// same event cannot be read again, unless someone deliberately rewinds the consumer group.
    /// </remarks>
    private static readonly TimeSpan CountedEventTimeToLive = TimeSpan.FromDays(3);

    private readonly Container _container = database.GetContainer(AnalyticsContainers.AggregatesName);

    public async Task ApplyAsync(MetricIncrement increment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(increment);

        var key = new PartitionKey(increment.Window.Id);
        foreach (var chunk in Operations(increment).Chunk(MaxPatchOperations))
        {
            try
            {
                await PatchAsync(increment.Window.Id, key, chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // The first write in this minute: the document does not exist yet. We create an empty one (409 means another instance
                // did it in the meantime) and repeat the Increment.
                await EnsureWindowAsync(increment.Window, cancellationToken).ConfigureAwait(false);
                await PatchAsync(increment.Window.Id, key, chunk, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <remarks>
    /// AZURE: a TransactionalBatch in the window's partition: creating the event marker and the counter Increment happen
    /// together or not at all. A second processing of the same event ends with 409 on the marker and rolls back the whole
    /// batch, so the counter does not grow. This costs a few RU per event, which is why we use it only where accuracy matters
    /// (money), not in the clickstream.
    /// </remarks>
    public async Task<bool> ApplyOnceAsync(Guid eventId, MetricIncrement increment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(increment);

        var operations = Operations(increment).ToList();
        if (operations.Count > MaxPatchOperations)
        {
            throw new ArgumentException($"A single event may change at most {MaxPatchOperations} counters.", nameof(increment));
        }

        for (var attempt = 1; ; attempt++)
        {
            var marker = new CountedEventDocument
            {
                Id = $"counted:{eventId:D}",
                MetricId = increment.Window.Id,
                TimeToLive = (int)CountedEventTimeToLive.TotalSeconds,
            };

            using var response = await _container
                .CreateTransactionalBatch(new PartitionKey(increment.Window.Id))
                .CreateItem(marker)
                .PatchItem(increment.Window.Id, operations)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, "metric_batch", response.RequestCharge);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response[0].StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }

            if (response[1].StatusCode == HttpStatusCode.NotFound && attempt == 1)
            {
                await EnsureWindowAsync(increment.Window, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // The Cosmos classifier treats 429/503 as transient and 400 as a permanent error.
            throw new CosmosException(
                $"Metric batch for window {increment.Window.Id} failed: {response.ErrorMessage}",
                response.StatusCode,
                0,
                response.ActivityId,
                response.RequestCharge);
        }
    }

    public async Task<IReadOnlyList<MetricWindowView>> ReadAsync(string metric, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var windows = WindowsBetween(metric, from, to).ToList();
        if (windows.Count == 0)
        {
            return [];
        }

        // AZURE: ReadMany groups point reads by physical partition and runs them in parallel. The cost is about 1 RU per existing
        // document, and missing minutes (no traffic) are simply not returned.
        var response = await _container
            .ReadManyItemsAsync<MetricWindowDocument>([.. windows.Select(window => (window.Id, new PartitionKey(window.Id)))], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        CosmosTelemetry.RecordRequestCharge(_container.Id, "read_many", response.RequestCharge);
        return [.. response.Where(document => document.DocType == MetricWindowDocument.DocumentType)
            .OrderBy(document => document.WindowStart)
            .Select(document => new MetricWindowView(document.WindowStart, document.Counters))];
    }

    internal static IEnumerable<MetricWindow> WindowsBetween(string metric, DateTimeOffset from, DateTimeOffset to)
    {
        var start = MetricWindow.For(metric, from).Start;
        var end = MetricWindow.For(metric, to).Start;
        for (var index = 0; start.AddMinutes(index) <= end && index < MaxWindowsPerRead; index++)
        {
            yield return new MetricWindow(metric, start.AddMinutes(index));
        }
    }

    /// <summary>JSON Patch path for a counter. The "~" and "/" characters in the name must be encoded (RFC 6901).</summary>
    internal static string CounterPath(string counter) =>
        "/counters/" + counter.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static IEnumerable<PatchOperation> Operations(MetricIncrement increment) =>
        increment.Counters.Select(counter => PatchOperation.Increment(CounterPath(counter.Key), counter.Value));

    private async Task PatchAsync(string id, PartitionKey key, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken)
    {
        var response = await _container
            .PatchItemAsync<MetricWindowDocument>(id, key, operations, new PatchItemRequestOptions { EnableContentResponseOnWrite = false }, cancellationToken)
            .ConfigureAwait(false);

        CosmosTelemetry.RecordRequestCharge(_container.Id, "metric_patch", response.RequestCharge);
    }

    private async Task EnsureWindowAsync(MetricWindow window, CancellationToken cancellationToken)
    {
        try
        {
            var document = new MetricWindowDocument
            {
                Id = window.Id,
                MetricId = window.Id,
                Metric = window.Metric,
                WindowStart = window.Start,
                TimeToLive = (int)WindowTimeToLive.TotalSeconds,
            };

            var response = await _container
                .CreateItemAsync(document, new PartitionKey(window.Id), new ItemRequestOptions { EnableContentResponseOnWrite = false }, cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, "metric_window_create", response.RequestCharge);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            CosmosTelemetry.RecordRequestCharge(_container.Id, "metric_window_create", exception.RequestCharge);
        }
    }
}
