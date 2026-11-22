using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts;

namespace ShopFlow.Messaging;

/// <summary>Correlation and causation of a new message.</summary>
public readonly record struct MessageMetadata(string CorrelationId, string? CausationId = null);

/// <summary>Builds an envelope from a contract object: assigns an identifier, serializes the payload, stores the trace context.</summary>
public sealed class MessageEnvelopeFactory(
    MessageTypeRegistry registry,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider)
{
    private readonly string _source = options.Value.ServiceName;

    /// <param name="message">Contract object.</param>
    /// <param name="metadata">Correlation and causation.</param>
    /// <param name="messageId">
    /// Optional deterministic identifier (see <see cref="DeterministicGuid"/>). Needed where a message is
    /// created outside the outbox and the same code may run again, e.g. in a Durable Functions activity.
    /// Sending again with the same identifier is rejected by the broker's duplicate detection.
    /// </param>
    public MessageEnvelope Create(IMessage message, MessageMetadata metadata, Guid? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var contractId = registry.GetContractId(message.GetType());
        var activity = Activity.Current;

        return new MessageEnvelope
        {
            // UUID v7 is time-ordered: outbox records and inbox entries are laid out in the index
            // in the order they were created.
            MessageId = messageId ?? Guid.CreateVersion7(timeProvider.GetUtcNow()),
            MessageType = contractId.Name,
            ContractVersion = contractId.Version,
            CorrelationId = metadata.CorrelationId,
            CausationId = metadata.CausationId,
            Source = _source,
            OccurredAt = timeProvider.GetUtcNow(),
            SessionId = message is IOrderScoped orderScoped ? orderScoped.OrderId.ToString("D") : null,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            Payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), ContractJson.Options),
        };
    }
}
