using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Domain.Orders;

/// <summary>Entry in the order's status history.</summary>
public sealed record OrderStatusChange(OrderStatus Status, DateTimeOffset At, string? Note);

/// <summary>
/// Full aggregate state for saving and rehydration (memento).
/// </summary>
/// <remarks>
/// WHY: the aggregate has no public setters, so the persistence layer cannot simply deserialize it. Instead of
/// opening up fields or adding JSON attributes to the domain, the aggregate exposes its state as one record.
/// Infrastructure maps that record to a Cosmos DB document and back, and the domain does not know what the
/// document looks like.
/// </remarks>
public sealed record OrderState(
    Guid OrderId,
    Guid CustomerId,
    string OrderNumber,
    IReadOnlyList<OrderLine> Lines,
    Money Total,
    Delivery Delivery,
    string PaymentMethodToken,
    OrderStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? ClosedAt,
    string? TrackingNumber,
    string? RejectionReasonCode,
    string? CancellationReason,
    IReadOnlyList<OrderStatusChange> History,
    string? ConcurrencyToken);
