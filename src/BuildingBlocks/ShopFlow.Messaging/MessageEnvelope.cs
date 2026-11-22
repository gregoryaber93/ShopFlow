using ShopFlow.Contracts;

namespace ShopFlow.Messaging;

/// <summary>
/// A broker-independent message: metadata plus the serialized payload.
/// </summary>
/// <remarks>
/// WHY: the envelope is the common format for the three places a message passes through: the outbox
/// record (Cosmos or SQL), the Service Bus message and the handler context. Mapping to a specific broker
/// therefore happens in one place, and the outbox can store the message exactly as it will be sent.
/// </remarks>
public sealed record MessageEnvelope
{
    /// <summary>Unique identifier. The deduplication key in the broker and in the consumer's inbox.</summary>
    public required Guid MessageId { get; init; }

    public required string MessageType { get; init; }

    public required int ContractVersion { get; init; }

    /// <summary>Constant for the whole business flow, e.g. from placing an order to its delivery.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Identifier of the message whose handling caused this one to be created.</summary>
    public string? CausationId { get; init; }

    public required string Source { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Ordering key. For order messages this is the order identifier.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// W3C trace context of the operation that produced the message.
    /// </summary>
    /// <remarks>
    /// WHY: there is no process continuity between writing to the outbox and publishing by the relay, so
    /// the distributed trace would stop at the database. We store traceparent with the message and the relay
    /// resumes the trace from it. Without this, the "show the whole journey of an order" query in Application
    /// Insights would end at the HTTP request.
    /// </remarks>
    public string? TraceParent { get; init; }

    public string? TraceState { get; init; }

    /// <summary>Payload as UTF-8 JSON, serialized with <see cref="ContractJson.Options"/>.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public MessageContractId ContractId => new(MessageType, ContractVersion);
}

/// <summary>Envelope together with the name of the queue or topic it should go to.</summary>
public sealed record OutboundMessage(MessageEnvelope Envelope, string Destination);
