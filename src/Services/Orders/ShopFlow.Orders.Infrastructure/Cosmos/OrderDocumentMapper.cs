using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Infrastructure.Cosmos;

internal static class OrderDocumentMapper
{
    public static OrderDocument ToDocument(Order order)
    {
        var state = order.ToState();
        var id = state.OrderId.ToString("D");

        return new OrderDocument
        {
            Id = id,
            OrderId = id,
            SchemaVersion = OrderDocument.CurrentSchemaVersion,
            CustomerId = state.CustomerId,
            OrderNumber = state.OrderNumber,
            Status = state.Status,
            Lines = [.. state.Lines.Select(line => new OrderLineDocument(line.Sku, line.ProductName, line.Quantity, ToDocument(line.UnitPrice)))],
            Total = ToDocument(state.Total),
            Delivery = new DeliveryDocument(
                state.Delivery.Method,
                state.Delivery.Address is { } address ? ToDocument(address) : null,
                state.Delivery.PickupPointId),
            PaymentMethodToken = state.PaymentMethodToken,
            SubmittedAt = state.SubmittedAt,
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            ClosedAt = state.ClosedAt,
            TrackingNumber = state.TrackingNumber,
            RejectionReasonCode = state.RejectionReasonCode,
            CancellationReason = state.CancellationReason,
            History = [.. state.History.Select(change => new StatusChangeDocument(change.Status, change.At, change.Note))],
        };
    }

    public static Order ToDomain(OrderDocument document) =>
        Order.Restore(new OrderState(
            Guid.Parse(document.OrderId),
            document.CustomerId,
            document.OrderNumber,
            [.. document.Lines.Select(line => new OrderLine(line.Sku, line.ProductName, line.Quantity, ToDomain(line.UnitPrice)))],
            ToDomain(document.Total),
            ReadDelivery(document),
            document.PaymentMethodToken,
            document.Status,
            document.SubmittedAt,
            document.ConfirmedAt,
            document.ShippedAt,
            document.ClosedAt,
            document.TrackingNumber,
            document.RejectionReasonCode,
            document.CancellationReason,
            [.. document.History.Select(change => new OrderStatusChange(change.Status, change.At, change.Note))],
            document.ETag));

    private static Delivery ReadDelivery(OrderDocument document) => document.SchemaVersion switch
    {
        // Schema 1: before pickup points were introduced every order was a courier delivery.
        1 => Delivery.Courier(ToDomain(document.ShippingAddress
            ?? throw new InvalidOperationException($"Order document {document.Id} (schema 1) has no shippingAddress."))),

        2 => Delivery.Restore(
            document.Delivery!.Method,
            document.Delivery.Address is { } address ? ToDomain(address) : null,
            document.Delivery.PickupPointId),

        _ => throw new InvalidOperationException(
            $"Order document {document.Id} has schema version {document.SchemaVersion}, newer than this code supports. " +
            "A newer version of the service has written it; roll forward instead of back."),
    };

    private static MoneyDocument ToDocument(Money money) => new(money.Amount, money.Currency);

    private static Money ToDomain(MoneyDocument money) => new(money.Amount, money.Currency);

    private static AddressDocument ToDocument(ShippingAddress address) =>
        new(address.RecipientName, address.Line1, address.Line2, address.City, address.PostalCode, address.CountryCode, address.Phone);

    private static ShippingAddress ToDomain(AddressDocument address) =>
        new(address.RecipientName, address.Line1, address.Line2, address.City, address.PostalCode, address.CountryCode, address.Phone);
}
