using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Application.Orders;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Application.Shipping;

/// <summary>The carrier picked up the parcel: the order moves to the Shipped state.</summary>
public sealed class ShipmentDispatchedHandler(IOrderRepository orders, OrderEventPublisher publisher)
    : IMessageHandler<ShipmentDispatched>
{
    public async Task HandleAsync(ShipmentDispatched message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var order = await ShipmentEventSupport.LoadAsync(orders, message.OrderId, cancellationToken).ConfigureAwait(false);
        var result = order.MarkShipped(message.TrackingNumber, message.DispatchedAt);
        await ShipmentEventSupport.ApplyAsync(order, result, orders, publisher, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The shipment was delivered: the order is completed and Orders publishes OrderCompleted.</summary>
public sealed class ShipmentDeliveredHandler(IOrderRepository orders, OrderEventPublisher publisher)
    : IMessageHandler<ShipmentDelivered>
{
    public async Task HandleAsync(ShipmentDelivered message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var order = await ShipmentEventSupport.LoadAsync(orders, message.OrderId, cancellationToken).ConfigureAwait(false);
        var result = order.MarkDelivered(message.DeliveredAt);
        await ShipmentEventSupport.ApplyAsync(order, result, orders, publisher, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Turns the result of a domain operation into a decision for the broker.
/// </summary>
/// <remarks>
/// This is the heart of poison message handling on the business side. The same domain refusal can mean two
/// different things:
/// <list type="bullet">
/// <item><see cref="OrderErrors.NotConfirmedYet"/>: too early. A transient exception; the message comes back
/// shortly, once the saga confirms the order.</item>
/// <item><see cref="OrderErrors.AlreadyClosed"/>: a rejected or cancelled order was delivered. A retry changes
/// nothing and the situation needs a human. The message goes straight to the DLQ with a clear reason.</item>
/// </list>
/// </remarks>
internal static class ShipmentEventSupport
{
    public static async Task<Order> LoadAsync(IOrderRepository orders, Guid orderId, CancellationToken cancellationToken) =>
        await orders.GetAsync(orderId, cancellationToken).ConfigureAwait(false)
        ?? throw new OutOfOrderMessageException($"Order {orderId} is not visible yet.");

    public static async Task ApplyAsync(
        Order order,
        Result result,
        IOrderRepository orders,
        OrderEventPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            throw result.Error.Code == OrderErrors.NotConfirmedYet.Code
                ? new OutOfOrderMessageException($"Order {order.Id} is {order.Status}; shipment event arrived before confirmation.")
                : new PermanentMessageException(
                    DeadLetterReasons.BusinessRuleViolation,
                    $"Order {order.Id} is {order.Status}: {result.Error.Code}. Shipment event requires manual investigation.");
        }

        // Save only when the state changes. A duplicate event ends here without touching the database.
        // On save the unit of work appends the inbox entry and, if any, OrderCompleted in one batch.
        if (await publisher.PublishAsync(order).ConfigureAwait(false))
        {
            await orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        }
    }
}
