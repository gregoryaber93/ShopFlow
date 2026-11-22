using System.Text.Json.Serialization;
using ShopFlow.Messaging.Inbox;

namespace ShopFlow.Persistence.Cosmos.Documents;

/// <summary>
/// Record of a processed message, saved in the same partition and transaction as the aggregate change.
/// </summary>
/// <remarks>
/// AZURE: Cosmos itself does the deduplication. A document identifier is unique within a logical partition,
/// so a second insert of the same <c>id</c> ends with 409 Conflict and rolls back the whole TransactionalBatch.
/// There is no separate "check whether we have seen it" step that would open a race between the check and
/// the write.
/// </remarks>
public sealed class InboxDocument : PartitionedSystemDocument
{
    public const string DocumentType = "inbox";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    [JsonPropertyName("messageId")]
    public required Guid MessageId { get; init; }

    [JsonPropertyName("consumer")]
    public required string Consumer { get; init; }

    [JsonPropertyName("processedAt")]
    public required DateTimeOffset ProcessedAt { get; init; }

    public static string CreateId(InboxEntry entry) => $"inbox:{entry.Consumer}:{entry.MessageId:D}";

    public static InboxDocument From(
        InboxEntry entry,
        string partitionKeyProperty,
        string partitionKeyValue,
        DateTimeOffset processedAt,
        TimeSpan timeToLive) =>
        new()
        {
            Id = CreateId(entry),
            MessageId = entry.MessageId,
            Consumer = entry.Consumer,
            ProcessedAt = processedAt,
            TimeToLive = (int)timeToLive.TotalSeconds,
            AdditionalProperties = PartitionKeyProperty(partitionKeyProperty, partitionKeyValue),
        };
}
