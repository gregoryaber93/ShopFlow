using System.Text.Json;
using System.Text.Json.Serialization;
using ShopFlow.Messaging;

namespace ShopFlow.Persistence.Cosmos.Documents;

/// <summary>
/// A message waiting to be published, saved in the partition of the aggregate that produced it.
/// </summary>
/// <remarks>
/// AZURE: the document is only ever inserted, never modified. The Change Feed in its default mode
/// ("latest version") shows the latest version of every changed document, so if events were a field
/// overwritten in the order document, two quick changes would produce one entry in the feed and one event
/// would be lost. A separate, immutable document per event appears in the Change Feed exactly once.
/// WHY: the relay does not mark the document as sent. The leases container remembers the position in the
/// feed and TTL takes care of clean-up. Publishing therefore costs no extra write.
/// </remarks>
public sealed class OutboxDocument : PartitionedSystemDocument
{
    public const string DocumentType = "outbox";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    [JsonPropertyName("messageId")]
    public required Guid MessageId { get; init; }

    [JsonPropertyName("messageType")]
    public required string MessageType { get; init; }

    [JsonPropertyName("contractVersion")]
    public required int ContractVersion { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("causationId")]
    public string? CausationId { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("traceParent")]
    public string? TraceParent { get; init; }

    [JsonPropertyName("traceState")]
    public string? TraceState { get; init; }

    /// <summary>Queue or topic the relay should send the message to.</summary>
    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    /// <summary>Payload as nested JSON, readable in Data Explorer and in diagnostic queries.</summary>
    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    public static string CreateId(Guid messageId) => $"outbox:{messageId:D}";

    public static OutboxDocument From(
        OutboundMessage message,
        string partitionKeyProperty,
        string partitionKeyValue,
        DateTimeOffset createdAt,
        TimeSpan timeToLive)
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = message.Envelope;
        using var payload = JsonDocument.Parse(envelope.Payload);

        return new OutboxDocument
        {
            Id = CreateId(envelope.MessageId),
            MessageId = envelope.MessageId,
            MessageType = envelope.MessageType,
            ContractVersion = envelope.ContractVersion,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Source = envelope.Source,
            OccurredAt = envelope.OccurredAt,
            SessionId = envelope.SessionId,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
            Destination = message.Destination,
            Payload = payload.RootElement.Clone(),
            CreatedAt = createdAt,
            TimeToLive = (int)timeToLive.TotalSeconds,
            AdditionalProperties = PartitionKeyProperty(partitionKeyProperty, partitionKeyValue),
        };
    }

    public OutboundMessage ToOutboundMessage() =>
        new(
            new MessageEnvelope
            {
                MessageId = MessageId,
                MessageType = MessageType,
                ContractVersion = ContractVersion,
                CorrelationId = CorrelationId,
                CausationId = CausationId,
                Source = Source,
                OccurredAt = OccurredAt,
                SessionId = SessionId,
                TraceParent = TraceParent,
                TraceState = TraceState,
                Payload = JsonSerializer.SerializeToUtf8Bytes(Payload),
            },
            Destination);
}
