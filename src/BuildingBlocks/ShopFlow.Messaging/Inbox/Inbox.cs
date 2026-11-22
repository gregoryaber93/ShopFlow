namespace ShopFlow.Messaging.Inbox;

/// <summary>
/// Record of a message having been processed by a specific consumer.
/// </summary>
/// <remarks>
/// WHY: the key is the (message, consumer) pair, not the message identifier alone. Several consumers
/// legitimately process the same event, and sometimes two of them share a database.
/// </remarks>
public readonly record struct InboxEntry(Guid MessageId, string Consumer);

/// <summary>
/// Explicit inbox for handlers that have no database transaction of their own (e.g. sending an e-mail).
/// </summary>
/// <remarks>
/// WHY: a handler that changes an aggregate does not use this interface. There the inbox entry is added
/// automatically to the same transaction as the aggregate (see the Cosmos and SQL adapters), which gives
/// an "exactly once" effect. Here the check and the side effect are separate steps, so the guarantee is
/// weaker and the handler chooses the order: entry before the effect (at most once) or after it (at least once).
/// </remarks>
public interface IInbox
{
    /// <summary>
    /// Saves the entry. Returns <c>false</c> if the message was already processed by this consumer.
    /// </summary>
    Task<bool> TryRegisterAsync(InboxEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by a database adapter when the inbox entry already exists, i.e. the message is a duplicate.
/// The dispatcher turns it into the <c>Duplicate</c> outcome and the message is completed.
/// </summary>
public sealed class DuplicateMessageException : Exception
{
    public DuplicateMessageException(InboxEntry entry)
        : base($"Message {entry.MessageId} has already been processed by '{entry.Consumer}'.")
    {
        Entry = entry;
    }

    public DuplicateMessageException(InboxEntry entry, Exception innerException)
        : base($"Message {entry.MessageId} has already been processed by '{entry.Consumer}'.", innerException)
    {
        Entry = entry;
    }

    public InboxEntry Entry { get; }
}
