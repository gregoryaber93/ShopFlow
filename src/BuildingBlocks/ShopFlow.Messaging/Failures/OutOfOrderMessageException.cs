namespace ShopFlow.Messaging.Failures;

/// <summary>
/// The message arrived too early: the state it refers to does not exist yet or is not visible yet.
/// A redelivery a moment later will most likely succeed.
/// </summary>
/// <remarks>
/// Typical causes:
/// <list type="bullet">
/// <item>Reordered messages from different sources (e.g. a shipping event before the order confirmation).</item>
/// <item>
/// AZURE: replication in Cosmos DB. With Session consistency the "read your own writes" guarantee
/// applies to one client session. Another process (another function app) can briefly read the state
/// from before the write, even in a single region. A document that "does not exist" may simply not
/// be visible to this reader yet.
/// </item>
/// </list>
/// WHY: a dedicated type instead of a generic exception, so that logs and metrics show how many retries
/// are caused by reordering. A growing number signals badly drawn context boundaries or Service Bus
/// sessions. When the state does not appear despite retries, the message lands in the DLQ with the
/// MaxDeliveryCountExceeded reason and an operator takes over (runbook dlq-growth).
/// </remarks>
public sealed class OutOfOrderMessageException : Exception
{
    public OutOfOrderMessageException(string message)
        : base(message)
    {
    }

    public OutOfOrderMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
