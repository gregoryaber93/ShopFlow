using System.Text;
using ShopFlow.Messaging;

namespace ShopFlow.Persistence.SqlServer.Messaging;

/// <summary>A row of the <c>messaging.OutboxMessages</c> table.</summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// Insertion order. Clustered key.
    /// </summary>
    /// <remarks>
    /// WHY: the clustered key is not <see cref="MessageId"/>. A random GUID as a clustered key scatters inserts
    /// across the whole index (page splits, fragmentation), while the poller always reads "oldest unpublished",
    /// i.e. a range at the start of the index. An increasing number gives appends at the end and in-order reads.
    /// </remarks>
    public long Sequence { get; private set; }

    public required Guid MessageId { get; init; }

    public required string MessageType { get; init; }

    public required int ContractVersion { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public required string Source { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? SessionId { get; init; }

    public string? TraceParent { get; init; }

    public string? TraceState { get; init; }

    public required string Destination { get; init; }

    /// <summary>Payload as JSON (<c>nvarchar(max)</c>), readable in diagnostic queries.</summary>
    public required string Payload { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Until when the record is claimed by a poller instance.</summary>
    public DateTimeOffset? ClaimedUntil { get; set; }

    public string? ClaimedBy { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public static OutboxMessage From(OutboundMessage message, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        var envelope = message.Envelope;

        return new OutboxMessage
        {
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
            Payload = Encoding.UTF8.GetString(envelope.Payload.Span),
            CreatedAt = createdAt,
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
                Payload = Encoding.UTF8.GetBytes(Payload),
            },
            Destination);
}

/// <summary>Row of the <c>messaging.InboxMessages</c> table. Primary key: (Consumer, MessageId).</summary>
public sealed class InboxMessage
{
    public required Guid MessageId { get; init; }

    public required string Consumer { get; init; }

    public required DateTimeOffset ProcessedAt { get; init; }
}
