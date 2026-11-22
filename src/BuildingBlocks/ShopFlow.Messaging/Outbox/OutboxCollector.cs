using ShopFlow.Contracts;
using ShopFlow.Messaging.Routing;

namespace ShopFlow.Messaging.Outbox;

/// <summary>
/// Buffer of messages produced during one operation (scoped).
/// </summary>
/// <remarks>
/// WHY: the envelope is built at <see cref="Enqueue"/> time, not at save time. At that moment we know the
/// current trace context and the causing message identifier, and a missing route for a message type
/// surfaces as an exception in business code rather than in the background in the relay.
/// </remarks>
internal sealed class OutboxCollector(
    MessageEnvelopeFactory envelopeFactory,
    IMessageRouter router,
    IMessageContextAccessor contextAccessor) : IOutbox, IPendingOutbox
{
    private readonly List<OutboundMessage> _pending = [];

    public bool HasPending => _pending.Count > 0;

    public void Enqueue(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var destination = router.GetDestination(message.GetType());
        var metadata = new MessageMetadata(
            contextAccessor.CorrelationId,
            contextAccessor.Current?.MessageId.ToString("D"));

        _pending.Add(new OutboundMessage(envelopeFactory.Create(message, metadata), destination));
    }

    public IReadOnlyList<OutboundMessage> Drain()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        OutboundMessage[] drained = [.. _pending];
        _pending.Clear();
        return drained;
    }
}
