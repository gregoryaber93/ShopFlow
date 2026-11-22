using ShopFlow.Contracts;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.OrderSaga.Model;

/// <summary>Event for a specific saga instance.</summary>
public sealed record SagaSignal(Guid OrderId, string EventName, SagaReply Reply)
{
    public string InstanceId => OrderSagaInstance.IdFor(OrderId);
}

/// <summary>
/// Translates participant events into orchestration external events. The list of types matches the "saga"
/// subscription filters on the topics (docs/architecture/message-flows.md).
/// </summary>
public static class SagaReplyMapper
{
    public static IReadOnlyList<Type> HandledTypes { get; } =
    [
        typeof(StockReserved), typeof(StockReservationFailed), typeof(StockReleased),
        typeof(PaymentAuthorized), typeof(PaymentDeclined), typeof(PaymentCaptured), typeof(PaymentCaptureFailed),
        typeof(PaymentVoided), typeof(PaymentRefunded),
        typeof(ShipmentCreated), typeof(ShipmentCreationFailed), typeof(ShipmentCancelled),
        typeof(OrderCancellationRequested),
    ];

    public static SagaSignal Map(IMessage message) => message switch
    {
        StockReserved m => new(m.OrderId, SagaEvents.ReserveStock, Success(m) with { WarehouseCode = m.WarehouseCode }),
        StockReservationFailed m => new(m.OrderId, SagaEvents.ReserveStock, Failure(m, m.ReasonCode)),
        StockReleased m => new(m.OrderId, SagaEvents.ReleaseStock, Success(m)),

        PaymentAuthorized m => new(m.OrderId, SagaEvents.AuthorizePayment, Success(m)),
        PaymentDeclined m => new(m.OrderId, SagaEvents.AuthorizePayment, Failure(m, m.DeclineCode)),
        PaymentCaptured m => new(m.OrderId, SagaEvents.CapturePayment, Success(m)),
        PaymentCaptureFailed m => new(m.OrderId, SagaEvents.CapturePayment, Failure(m, m.ReasonCode)),
        PaymentVoided m => new(m.OrderId, SagaEvents.VoidPayment, Success(m)),
        PaymentRefunded m => new(m.OrderId, SagaEvents.RefundPayment, Success(m)),

        ShipmentCreated m => new(m.OrderId, SagaEvents.CreateShipment, Success(m) with { TrackingNumber = m.TrackingNumber }),
        ShipmentCreationFailed m => new(m.OrderId, SagaEvents.CreateShipment, Failure(m, m.ReasonCode)),
        ShipmentCancelled m => new(m.OrderId, SagaEvents.CancelShipment, Success(m)),

        OrderCancellationRequested m => new(m.OrderId, SagaEvents.CancellationRequested, Success(m) with { ReasonCode = m.Reason }),

        _ => throw new ArgumentException($"{message.GetType().Name} is not a saga reply.", nameof(message)),
    };

    private static SagaReply Success(IMessage message) => new() { Succeeded = true, ReplyType = message.GetType().Name };

    private static SagaReply Failure(IMessage message, string reasonCode) =>
        new() { Succeeded = false, ReplyType = message.GetType().Name, ReasonCode = reasonCode };
}
