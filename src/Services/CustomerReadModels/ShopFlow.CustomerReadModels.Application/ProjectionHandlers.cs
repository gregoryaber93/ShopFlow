using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using OrdersV2 = ShopFlow.Contracts.Orders.V2;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.CustomerReadModels.Application;

public static class CustomerOrderProjectionRegistration
{
    /// <summary>Registers every event that changes the <c>customer-orders</c> view.</summary>
    public static MessagingBuilder AddCustomerOrderProjection(this MessagingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddHandler<OrdersV2.OrderSubmitted, CustomerOrderProjectionHandlers>()
            .AddHandler<OrdersV1.OrderConfirmed, CustomerOrderProjectionHandlers>()
            .AddHandler<OrdersV1.OrderRejected, CustomerOrderProjectionHandlers>()
            .AddHandler<OrdersV1.OrderCancellationRequested, CustomerOrderProjectionHandlers>()
            .AddHandler<OrdersV1.OrderCancelled, CustomerOrderProjectionHandlers>()
            .AddHandler<OrdersV1.OrderCompleted, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.PaymentAuthorized, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.PaymentDeclined, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.PaymentCaptured, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.PaymentCaptureFailed, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.PaymentVoided, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.PaymentRefunded, CustomerOrderProjectionHandlers>()
            .AddHandler<PaymentsV1.InvoiceIssued, CustomerOrderProjectionHandlers>()
            .AddHandler<ShippingV1.ShipmentCreated, CustomerOrderProjectionHandlers>()
            .AddHandler<ShippingV1.ShipmentCancelled, CustomerOrderProjectionHandlers>()
            .AddHandler<ShippingV1.ShipmentDispatched, CustomerOrderProjectionHandlers>()
            .AddHandler<ShippingV1.ShipmentDelivered, CustomerOrderProjectionHandlers>();
    }
}

/// <summary>
/// Handlers of the <c>customer-orders</c> projection. Each one only says which view method handles the event.
/// </summary>
/// <remarks>
/// Deliberately without handlers:
/// <list type="bullet">
/// <item><c>OrderSubmitted</c> v1: during the sunset period Orders publishes both versions, and the projection subscribes
/// only to the one it understands (v2). Version 1 is skipped by the consumer's <c>Ignore</c> policy.</item>
/// <item><c>ShipmentCreationFailed</c>: the customer sees the effect as a rejected order (<c>OrderRejected</c>).</item>
/// </list>
/// </remarks>
public sealed class CustomerOrderProjectionHandlers(CustomerOrderProjector projector) :
    IMessageHandler<OrdersV2.OrderSubmitted>,
    IMessageHandler<OrdersV1.OrderConfirmed>,
    IMessageHandler<OrdersV1.OrderRejected>,
    IMessageHandler<OrdersV1.OrderCancellationRequested>,
    IMessageHandler<OrdersV1.OrderCancelled>,
    IMessageHandler<OrdersV1.OrderCompleted>,
    IMessageHandler<PaymentsV1.PaymentAuthorized>,
    IMessageHandler<PaymentsV1.PaymentDeclined>,
    IMessageHandler<PaymentsV1.PaymentCaptured>,
    IMessageHandler<PaymentsV1.PaymentCaptureFailed>,
    IMessageHandler<PaymentsV1.PaymentVoided>,
    IMessageHandler<PaymentsV1.PaymentRefunded>,
    IMessageHandler<PaymentsV1.InvoiceIssued>,
    IMessageHandler<ShippingV1.ShipmentCreated>,
    IMessageHandler<ShippingV1.ShipmentCancelled>,
    IMessageHandler<ShippingV1.ShipmentDispatched>,
    IMessageHandler<ShippingV1.ShipmentDelivered>
{
    public Task HandleAsync(OrdersV2.OrderSubmitted message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderConfirmed message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderRejected message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderCancellationRequested message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderCancelled message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderCompleted message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.PaymentAuthorized message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.PaymentDeclined message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.PaymentCaptured message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.PaymentCaptureFailed message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.PaymentVoided message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.PaymentRefunded message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(PaymentsV1.InvoiceIssued message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(ShippingV1.ShipmentCreated message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(ShippingV1.ShipmentCancelled message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(ShippingV1.ShipmentDispatched message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }

    public Task HandleAsync(ShippingV1.ShipmentDelivered message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return projector.ProjectAsync(message.CustomerId, message.OrderId, context, view => view.Apply(message), cancellationToken);
    }
}
