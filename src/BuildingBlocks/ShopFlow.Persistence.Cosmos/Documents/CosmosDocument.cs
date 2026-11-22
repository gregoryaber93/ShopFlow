using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopFlow.Persistence.Cosmos.Documents;

/// <summary>
/// Fields common to every document saved by ShopFlow.
/// </summary>
/// <remarks>
/// AZURE: a Cosmos DB container has no schema, so one container holds documents of different types
/// (aggregate, outbox, inbox) told apart by the <c>docType</c> field. There is one condition: documents
/// that must be written atomically must have the same partition key.
/// </remarks>
public abstract class CosmosDocument
{
    /// <summary>Unique within a logical partition (not within the whole container).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("docType")]
    public abstract string DocType { get; }

    /// <summary>
    /// Version token assigned by Cosmos on every write. Sending it back in the If-Match header
    /// implements optimistic concurrency.
    /// </summary>
    [JsonPropertyName("_etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; init; }

    /// <summary>Time to live in seconds. <c>null</c> means no expiry.</summary>
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLive { get; init; }
}

/// <summary>
/// Infrastructure document (outbox, inbox) that must land in the aggregate's partition even though it
/// does not know in advance what the container's partition key field is called.
/// </summary>
public abstract class PartitionedSystemDocument : CosmosDocument
{
    /// <summary>
    /// Additional document fields. On write it contains the partition key field (e.g. <c>orderId</c>);
    /// on read it also contains Cosmos system fields (<c>_rid</c>, <c>_ts</c>, <c>_lsn</c>).
    /// </summary>
    /// <remarks>
    /// WHY: the same outbox class works in a container partitioned by <c>/orderId</c> and by
    /// <c>/customerId</c>. The unit of work supplies the field name, and <see cref="JsonExtensionDataAttribute"/>
    /// writes it as a regular top-level field of the document.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = [];

    protected static Dictionary<string, JsonElement> PartitionKeyProperty(string name, string value) =>
        new(StringComparer.Ordinal) { [name] = JsonSerializer.SerializeToElement(value) };
}
