using ShopFlow.Messaging.Inbox;

namespace ShopFlow.Messaging;

/// <summary>What to do with a message for which the consumer has no handler.</summary>
public enum UnhandledMessagePolicy
{
    /// <summary>
    /// Move it to the DLQ. Right for command queues: a command without a handler means a routing error
    /// or a consumer version that has not been deployed yet, and it must not silently disappear.
    /// </summary>
    DeadLetter = 0,

    /// <summary>
    /// Complete and skip. Right for consumers that subscribe to "everything" (read models, analytics),
    /// which by definition also receive events they do not care about.
    /// </summary>
    Ignore = 1,
}

/// <summary>Logical consumer: its name (part of the inbox key) and the policy for unknown messages.</summary>
public sealed record ConsumerEndpoint(
    string Name,
    UnhandledMessagePolicy UnhandledPolicy = UnhandledMessagePolicy.DeadLetter);

/// <summary>Information about a specific delivery attempt, known only to the broker.</summary>
/// <param name="DeliveryCount">Which delivery attempt this is (starting at 1).</param>
/// <param name="EnqueuedAt">When the broker accepted the message.</param>
/// <param name="IsReplay">Whether an operator replayed the message from the DLQ.</param>
public readonly record struct DeliveryInfo(int DeliveryCount, DateTimeOffset EnqueuedAt, bool IsReplay = false);

/// <summary>Context passed to the handler together with the message.</summary>
public sealed record MessageContext
{
    public required Guid MessageId { get; init; }

    public required string MessageType { get; init; }

    public required int ContractVersion { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public required string Source { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? SessionId { get; init; }

    public required string Consumer { get; init; }

    public required int DeliveryCount { get; init; }

    public required DateTimeOffset EnqueuedAt { get; init; }

    public bool IsReplay { get; init; }

    /// <summary>Entry that must be saved in the same transaction as the state change.</summary>
    public InboxEntry InboxEntry => new(MessageId, Consumer);

    public static MessageContext From(MessageEnvelope envelope, ConsumerEndpoint endpoint, DeliveryInfo delivery)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(endpoint);

        return new MessageContext
        {
            MessageId = envelope.MessageId,
            MessageType = envelope.MessageType,
            ContractVersion = envelope.ContractVersion,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Source = envelope.Source,
            OccurredAt = envelope.OccurredAt,
            SessionId = envelope.SessionId,
            Consumer = endpoint.Name,
            DeliveryCount = delivery.DeliveryCount,
            EnqueuedAt = delivery.EnqueuedAt,
            IsReplay = delivery.IsReplay,
        };
    }
}

/// <summary>Access to the context of the current operation (handling a message or an HTTP request).</summary>
public interface IMessageContextAccessor
{
    /// <summary>The message being handled, or <c>null</c> when an HTTP request started the operation.</summary>
    MessageContext? Current { get; }

    /// <summary>Correlation identifier inherited by messages sent during this operation.</summary>
    string CorrelationId { get; }
}

/// <summary>
/// Scoped implementation: one instance per handled message or per HTTP request.
/// </summary>
/// <remarks>
/// WHY: deliberately not AsyncLocal. A DI scope is explicit and has a clear end, while AsyncLocal can
/// "leak" into background tasks and attach another operation's correlation to them.
/// </remarks>
public sealed class MessageContextAccessor : IMessageContextAccessor
{
    private string? _correlationId;

    public MessageContext? Current { get; private set; }

    public string CorrelationId =>
        Current?.CorrelationId ?? (_correlationId ??= Guid.CreateVersion7().ToString("D"));

    public void SetIncoming(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Current = context;
    }

    /// <summary>Set by the HTTP middleware from the <c>X-Correlation-Id</c> header.</summary>
    public void SetCorrelationId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        _correlationId = correlationId;
    }
}
