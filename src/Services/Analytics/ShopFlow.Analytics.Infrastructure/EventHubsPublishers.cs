using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using ShopFlow.Analytics.Application;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Analytics.V1;

namespace ShopFlow.Analytics.Infrastructure;

/// <summary>
/// Connection to the Event Hubs namespace.
/// </summary>
/// <remarks>
/// AZURE: the same <c>EventHubs</c> section configures the function triggers (<c>Connection = "EventHubs"</c> looks for the
/// <c>EventHubs__fullyQualifiedNamespace</c> setting and connects with the managed identity). The namespace has
/// <c>disableLocalAuth = true</c>, so a connection string with a SAS key will not work.
/// </remarks>
public sealed class EventHubsOptions
{
    public const string SectionName = "EventHubs";

    /// <summary>E.g. <c>evhns-shopflow-prod-weu.servicebus.windows.net</c>.</summary>
    [Required]
    public string FullyQualifiedNamespace { get; set; } = string.Empty;
}

/// <summary>Producer for one hub. The wrapper allows registering two clients in DI without named registrations.</summary>
public sealed class HubProducer<THub>(EventHubProducerClient client) : IAsyncDisposable
    where THub : IHubName
{
    public EventHubProducerClient Client { get; } = client;

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

public interface IHubName
{
    static abstract string Name { get; }
}

public sealed class ClickstreamHub : IHubName
{
    public static string Name => MessagingTopology.EventHubs.Clickstream;
}

public sealed class BusinessEventsHub : IHubName
{
    public static string Name => MessagingTopology.EventHubs.BusinessEvents;
}

internal static class EventHubBatchSender
{
    private static readonly JsonSerializerOptions Json = ContractJson.Options;

    /// <summary>
    /// Sends events grouped by partition key, in batches that fit within the size limit.
    /// </summary>
    /// <remarks>
    /// AZURE: the partition key is set on the whole batch (<see cref="CreateBatchOptions.PartitionKey"/>), not on an individual
    /// event, so events with different keys must go in separate batches. Event Hubs hashes the key and always picks the same
    /// partition, which preserves the order of one session's (one order's) events.
    /// <para>
    /// WHY: a plain producer instead of <c>EventHubBufferedProducerClient</c>. The buffer collects events and sends them in the
    /// background, which gives better throughput, but the call returns before the write to Event Hubs. In the HTTP function a
    /// 202 before the durable write would mean silent data loss on an instance restart, and in the forwarder it would complete
    /// a Service Bus message that nobody has written yet.
    /// </para>
    /// <para>
    /// AZURE: the batch's <c>MaximumSizeInBytes</c> depends on the tier (1 MB in Standard). <c>TryAdd</c> returns false when
    /// the event does not fit: then we send the current batch and start a new one.
    /// </para>
    /// </remarks>
    public static async Task SendAsync<T>(
        EventHubProducerClient producer,
        IReadOnlyList<T> items,
        Func<T, string> partitionKey,
        Func<T, EventData> toEventData,
        CancellationToken cancellationToken)
    {
        foreach (var group in items.GroupBy(partitionKey))
        {
            var options = new CreateBatchOptions { PartitionKey = group.Key };
            var batch = await producer.CreateBatchAsync(options, cancellationToken).ConfigureAwait(false);

            try
            {
                foreach (var item in group)
                {
                    var eventData = toEventData(item);
                    if (batch.TryAdd(eventData))
                    {
                        continue;
                    }

                    if (batch.Count == 0)
                    {
                        throw new InvalidOperationException("A single event exceeds the maximum Event Hubs batch size.");
                    }

                    await producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Dispose();
                    batch = await producer.CreateBatchAsync(options, cancellationToken).ConfigureAwait(false);

                    if (!batch.TryAdd(eventData))
                    {
                        throw new InvalidOperationException("A single event exceeds the maximum Event Hubs batch size.");
                    }
                }

                if (batch.Count > 0)
                {
                    await producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                batch.Dispose();
            }
        }
    }

    public static EventData Create<T>(T value, Guid eventId, string eventType, int? contractVersion)
    {
        var eventData = new EventData(BinaryData.FromObjectAsJson(value, Json))
        {
            // WHY: the identifier and type also go into the properties, not only into the body. A consumer (or a query over the
            // Capture archive) can filter events without parsing JSON, and the MessageId is used for deduplication.
            MessageId = eventId.ToString("D"),
            ContentType = "application/json",
        };

        eventData.Properties["eventType"] = eventType;
        if (contractVersion is { } version)
        {
            eventData.Properties["contractVersion"] = version;
        }

        return eventData;
    }
}

internal sealed class EventHubClickstreamPublisher(HubProducer<ClickstreamHub> hub) : IClickstreamPublisher
{
    public Task PublishAsync(IReadOnlyList<ClickstreamEvent> events, CancellationToken cancellationToken) =>
        EventHubBatchSender.SendAsync(
            hub.Client,
            events,
            clickstreamEvent => clickstreamEvent.SessionId,
            clickstreamEvent => EventHubBatchSender.Create(clickstreamEvent, clickstreamEvent.EventId, clickstreamEvent.EventType, contractVersion: null),
            cancellationToken);
}

internal sealed class EventHubBusinessEventPublisher(HubProducer<BusinessEventsHub> hub) : IBusinessEventPublisher
{
    public Task PublishAsync(IReadOnlyList<BusinessEvent> events, CancellationToken cancellationToken) =>
        EventHubBatchSender.SendAsync(
            hub.Client,
            events,
            BusinessEventMapper.PartitionKey,
            businessEvent => EventHubBatchSender.Create(businessEvent, businessEvent.EventId, businessEvent.EventType, businessEvent.ContractVersion),
            cancellationToken);
}
