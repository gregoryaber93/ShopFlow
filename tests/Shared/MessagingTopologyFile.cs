using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopFlow.Tests.Shared;

/// <summary>
/// Model of the <c>infra/modules/messaging/messaging-topology.json</c> file, shared by the contract and architecture tests
/// (the source file is linked into both projects).
/// </summary>
internal sealed record MessagingTopologyFile(
    [property: JsonPropertyName("serviceBus")] ServiceBusTopology ServiceBus,
    [property: JsonPropertyName("eventHubs")] EventHubsTopology EventHubs)
{
    public const string FileName = "messaging-topology.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MessagingTopologyFile Load() =>
        JsonSerializer.Deserialize<MessagingTopologyFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, FileName)), Json)
        ?? throw new InvalidOperationException($"{FileName} is empty.");
}

internal sealed record ServiceBusTopology(IReadOnlyList<QueueDefinition> Queues, IReadOnlyList<TopicDefinition> Topics);

internal sealed record QueueDefinition(string Name, bool RequiresSession, string? DuplicateDetectionWindow, int MaxDeliveryCount, string LockDuration, string Consumer, IReadOnlyList<string>? Senders, IReadOnlyList<string> Messages);

internal sealed record TopicDefinition(string Name, string? DuplicateDetectionWindow, string Publisher, IReadOnlyList<string> Messages, IReadOnlyList<SubscriptionDefinition> Subscriptions);

internal sealed record SubscriptionDefinition(string Name, FilterDefinition? Filter, string? ForwardTo, string? Consumer, int MaxDeliveryCount)
{
    /// <summary>Message types that pass the subscription filter on a given topic.</summary>
    public IReadOnlyList<string> AcceptedMessages(TopicDefinition topic) =>
        Filter switch
        {
            null => topic.Messages,
            { Subjects: { } subjects } => subjects,
            { Correlation: { } correlation } => [correlation.Subject],
            _ => topic.Messages,
        };
}

internal sealed record FilterDefinition(IReadOnlyList<string>? Subjects, CorrelationFilterDefinition? Correlation);

internal sealed record CorrelationFilterDefinition(string Subject, IReadOnlyDictionary<string, string>? Properties);

internal sealed record EventHubsTopology(IReadOnlyList<HubDefinition> Hubs);

internal sealed record HubDefinition(string Name, int PartitionCount, int RetentionHours, string PartitionKey, bool Capture, IReadOnlyList<string> ConsumerGroups);
