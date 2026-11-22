namespace ShopFlow.Contracts;

/// <summary>Marker for every message sent between services.</summary>
public interface IMessage;

/// <summary>
/// Command: a request to perform an operation, addressed to exactly one receiver.
/// Named in the imperative mood (<c>ReserveStock</c>). Sent to a queue.
/// </summary>
public interface ICommand : IMessage;

/// <summary>
/// Integration event: a fact that has already happened. The publisher does not know who listens.
/// Named in the past tense (<c>StockReserved</c>). Published to a topic.
/// </summary>
public interface IIntegrationEvent : IMessage;

/// <summary>
/// Message about a single order.
/// </summary>
/// <remarks>
/// AZURE: <see cref="OrderId"/> becomes the <c>SessionId</c> in Service Bus. Messages with the
/// same SessionId are delivered in order and to only one receiver at a time, which gives ordering
/// within an order without serializing the whole queue globally.
/// WHY: <see cref="CustomerId"/> is mandatory even where the receiver does not need it. The read
/// model is partitioned by customer and the projector receives only the event. Without this field
/// it would have to run a cross-partition query to find the document to update.
/// </remarks>
public interface IOrderScoped
{
    Guid OrderId { get; }

    Guid CustomerId { get; }
}
