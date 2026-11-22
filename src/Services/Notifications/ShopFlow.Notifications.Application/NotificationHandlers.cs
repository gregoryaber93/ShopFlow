using System.Globalization;
using Microsoft.Extensions.Logging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using InventoryV1 = ShopFlow.Contracts.Inventory.V1;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.Notifications.Application;

// WHY: handlers are thin: they decide whether and to whom to send, and pick the template. Idempotency, the recipient,
// the language and recording the outcome are handled by NotificationSender, so every new kind of notification is a few lines.

public sealed class OrderNotificationHandlers(NotificationSender sender, NotificationTemplates templates) :
    IMessageHandler<OrdersV1.OrderConfirmed>,
    IMessageHandler<OrdersV1.OrderRejected>,
    IMessageHandler<OrdersV1.OrderCancelled>
{
    public Task HandleAsync(OrdersV1.OrderConfirmed message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.OrderConfirmed, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.OrderConfirmed(message, recipient),
            cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderRejected message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.OrderRejected, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.OrderRejected(message, recipient),
            cancellationToken);
    }

    public Task HandleAsync(OrdersV1.OrderCancelled message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.OrderCancelled, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.OrderCancelled(message, recipient),
            cancellationToken);
    }
}

public sealed partial class PaymentNotificationHandlers(
    NotificationSender sender,
    NotificationTemplates templates,
    IInvoiceLinks invoiceLinks,
    ILogger<PaymentNotificationHandlers> logger) :
    IMessageHandler<PaymentsV1.PaymentRefunded>,
    IMessageHandler<PaymentsV1.InvoiceIssued>
{
    public Task HandleAsync(PaymentsV1.PaymentRefunded message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // WHY: a refund of 0 is Payments' reply to a compensation where no money was captured and only the authorization hold
        // was released. An e-mail saying "we refunded 0.00 PLN" would be confusing, and the customer gets a separate message
        // about the cancellation anyway (OrderCancelled or OrderRejected).
        if (message.Amount.Amount <= 0m)
        {
            LogZeroRefundSkipped(message.OrderId);
            return Task.CompletedTask;
        }

        return sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.RefundIssued, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.RefundIssued(message, recipient),
            cancellationToken);
    }

    public async Task HandleAsync(PaymentsV1.InvoiceIssued message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // WHY: the link is generated on every attempt and stored nowhere. The event carries only the blob path
        // (see the InvoiceIssued contract), and the link with the SAS token exists only in the sent e-mail.
        var link = await invoiceLinks.CreateAsync(message.BlobPath, cancellationToken).ConfigureAwait(false);

        await sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.InvoiceIssued, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.InvoiceIssued(message, link, invoiceLinks.Lifetime, recipient),
            cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund of zero for order {OrderId} (authorization hold released), no email sent")]
    private partial void LogZeroRefundSkipped(Guid orderId);
}

public sealed class ShipmentNotificationHandlers(
    NotificationSender sender,
    NotificationTemplates templates,
    INotificationFeatures features) :
    IMessageHandler<ShippingV1.ShipmentDispatched>,
    IMessageHandler<ShippingV1.ShipmentDelivered>
{
    public async Task HandleAsync(ShippingV1.ShipmentDispatched message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.ShipmentDispatched, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.ShipmentDispatched(message, recipient),
            cancellationToken).ConfigureAwait(false);

        // WHY: the flag is evaluated per customer (targeting in App Configuration), not globally. SMS is billed per segment, so a
        // new channel is first enabled for a small percentage of customers while we watch costs and complaints.
        // The same customer always lands in the same group, because the assignment is computed from a hash of their identifier.
        // WHY: e-mail and SMS are two independent notifications with separate history entries. When the SMS fails transiently,
        // redelivering the message does not send the e-mail again, because its entry already has an outcome.
        if (await features.IsShipmentSmsEnabledAsync(message.CustomerId).ConfigureAwait(false))
        {
            await sender.SendToCustomerAsync(
                NotificationDraft.ForCustomer(context, TemplateNames.ShipmentDispatched, NotificationChannel.Sms, message.CustomerId, message.OrderId),
                message.CustomerId,
                recipient => templates.ShipmentDispatchedSms(message, recipient),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task HandleAsync(ShippingV1.ShipmentDelivered message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sender.SendToCustomerAsync(
            NotificationDraft.ForCustomer(context, TemplateNames.ShipmentDelivered, NotificationChannel.Email, message.CustomerId, message.OrderId),
            message.CustomerId,
            recipient => templates.ShipmentDelivered(message, recipient),
            cancellationToken);
    }
}

public sealed class StockAlertHandler(NotificationSender sender, NotificationTemplates templates) : IMessageHandler<InventoryV1.StockLevelLow>
{
    public Task HandleAsync(InventoryV1.StockLevelLow message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // One alert per SKU, warehouse and day (UTC). Stock can cross the threshold many times in both directions
        // (reservation, release, reservation again), and the operations team does not need ten e-mails a day.
        var businessKey = string.Create(CultureInfo.InvariantCulture, $"{message.WarehouseCode}/{message.Sku}/{message.DetectedAt.UtcDateTime:yyyy-MM-dd}");

        return sender.SendToOperationsAsync(
            NotificationDraft.ForOperations(context, TemplateNames.StockLevelLow, businessKey),
            _ => templates.StockLevelLow(message),
            cancellationToken);
    }
}
