namespace ShopFlow.Contracts.Orders;

/// <summary>
/// Lossless upgrade of <c>OrderSubmitted</c> from version 1 to 2: a version 1 order was always
/// a courier delivery to an address.
/// </summary>
public sealed class OrderSubmittedV1ToV2Upcaster : IMessageUpcaster<V1.OrderSubmitted, V2.OrderSubmitted>
{
    public V2.OrderSubmitted Upcast(V1.OrderSubmitted message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new V2.OrderSubmitted
        {
            OrderId = message.OrderId,
            CustomerId = message.CustomerId,
            OrderNumber = message.OrderNumber,
            Lines = message.Lines,
            Total = message.Total,
            Delivery = new V2.DeliveryDto
            {
                Method = V2.DeliveryMethod.Courier,
                Address = message.ShippingAddress,
            },
            PaymentMethodToken = message.PaymentMethodToken,
            SubmittedAt = message.SubmittedAt,
        };
    }
}
