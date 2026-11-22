using ShopFlow.Contracts;

namespace ShopFlow.Messaging.Outbox;

/// <summary>
/// Port used by the Application layer to "send" a message.
/// </summary>
/// <remarks>
/// WHY: <see cref="Enqueue"/> sends nothing. The message goes to a buffer scoped to the current operation,
/// and the database adapter saves it in the same transaction as the aggregate change. A separate process
/// (the relay) publishes it to the broker. There is therefore no "order saved, event not sent" state and no
/// "event sent, save failed" state.
/// </remarks>
public interface IOutbox
{
    void Enqueue(IMessage message);
}

/// <summary>
/// Infrastructure side of the outbox: the database adapter takes the messages to save from here.
/// </summary>
public interface IPendingOutbox
{
    bool HasPending { get; }

    /// <summary>Returns the collected messages and empties the buffer.</summary>
    IReadOnlyList<OutboundMessage> Drain();
}

/// <summary>Publishes envelopes to the broker. Called by the relay, never by business logic.</summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Sends messages, preserving order within a single destination.
    /// The method completes only after the broker has acknowledged them.
    /// </summary>
    Task PublishAsync(IReadOnlyCollection<OutboundMessage> messages, CancellationToken cancellationToken);
}
