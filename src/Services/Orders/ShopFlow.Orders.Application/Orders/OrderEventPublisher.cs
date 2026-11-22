using ShopFlow.Contracts.Common;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Domain;
using V1 = ShopFlow.Contracts.Orders.V1;
using V2 = ShopFlow.Contracts.Orders.V2;

namespace ShopFlow.Orders.Application.Orders;

/// <summary>
/// Translates the order's domain events into integration events and puts them into the outbox.
/// </summary>
/// <remarks>
/// WHY: this is the only place where the Orders domain model meets public contracts. Renaming a field in the
/// aggregate changes only this file, not every consumer of the events.
/// </remarks>
public sealed class OrderEventPublisher(IOutbox outbox, IOrderFeatures features)
{
    /// <returns><c>true</c> if the aggregate had events, i.e. there is something to save.</returns>
    public async Task<bool> PublishAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var events = order.DequeueDomainEvents();

        foreach (var domainEvent in events)
        {
            switch (domainEvent)
            {
                case OrderSubmittedDomainEvent:
                    // Version 2 always; the saga subscribes to it only (filter ContractVersion = '2').
                    outbox.Enqueue(ToSubmittedV2(order));

                    // Version 1 side by side until it is retired. A pickup-point order cannot be expressed in v1, which is why
                    // that delivery method is blocked while v1 is still being published.
                    if (order.Delivery.Method == DeliveryMethod.Courier && await features.PublishOrderSubmittedV1Async().ConfigureAwait(false))
                    {
                        outbox.Enqueue(ToSubmittedV1(order));
                    }

                    break;

                case OrderCancellationRequestedDomainEvent cancellation:
                    outbox.Enqueue(new V1.OrderCancellationRequested
                    {
                        OrderId = order.Id,
                        CustomerId = order.CustomerId,
                        RequestedBy = cancellation.RequestedBy,
                        Reason = cancellation.Reason,
                        RequestedAt = cancellation.OccurredAt,
                    });
                    break;

                case OrderConfirmedDomainEvent confirmed:
                    outbox.Enqueue(new V1.OrderConfirmed
                    {
                        OrderId = order.Id,
                        CustomerId = order.CustomerId,
                        OrderNumber = order.OrderNumber,
                        Total = ToDto(order.Total),
                        ConfirmedAt = confirmed.OccurredAt,
                        TrackingNumber = order.TrackingNumber,
                    });
                    break;

                case OrderRejectedDomainEvent rejected:
                    outbox.Enqueue(new V1.OrderRejected
                    {
                        OrderId = order.Id,
                        CustomerId = order.CustomerId,
                        OrderNumber = order.OrderNumber,
                        ReasonCode = rejected.ReasonCode,
                        ReasonDescription = rejected.Description,
                        RejectedAt = rejected.OccurredAt,
                    });
                    break;

                case OrderCancelledDomainEvent cancelled:
                    outbox.Enqueue(new V1.OrderCancelled
                    {
                        OrderId = order.Id,
                        CustomerId = order.CustomerId,
                        OrderNumber = order.OrderNumber,
                        Reason = cancelled.Reason,
                        CancelledAt = cancelled.OccurredAt,
                    });
                    break;

                case OrderCompletedDomainEvent completed:
                    outbox.Enqueue(new V1.OrderCompleted
                    {
                        OrderId = order.Id,
                        CustomerId = order.CustomerId,
                        OrderNumber = order.OrderNumber,
                        CompletedAt = completed.OccurredAt,
                    });
                    break;

                case OrderShippedDomainEvent:
                    // No integration event: whoever is interested listens to ShipmentDispatched directly from Shipping.
                    // Re-announcing someone else's fact only doubles the traffic.
                    break;

                default:
                    throw new InvalidOperationException($"Domain event {domainEvent.GetType().Name} has no integration mapping.");
            }
        }

        return events.Count > 0;
    }

    private static V2.OrderSubmitted ToSubmittedV2(Order order) => new()
    {
        OrderId = order.Id,
        CustomerId = order.CustomerId,
        OrderNumber = order.OrderNumber,
        Lines = ToLines(order),
        Total = ToDto(order.Total),
        Delivery = new V2.DeliveryDto
        {
            Method = order.Delivery.Method == DeliveryMethod.Courier ? V2.DeliveryMethod.Courier : V2.DeliveryMethod.PickupPoint,
            Address = order.Delivery.Address is { } address ? ToDto(address) : null,
            PickupPointId = order.Delivery.PickupPointId,
        },
        PaymentMethodToken = order.PaymentMethodToken,
        SubmittedAt = order.SubmittedAt,
    };

    private static V1.OrderSubmitted ToSubmittedV1(Order order) => new()
    {
        OrderId = order.Id,
        CustomerId = order.CustomerId,
        OrderNumber = order.OrderNumber,
        Lines = ToLines(order),
        Total = ToDto(order.Total),
        ShippingAddress = ToDto(order.Delivery.Address!),
        PaymentMethodToken = order.PaymentMethodToken,
        SubmittedAt = order.SubmittedAt,
    };

    private static OrderLineDto[] ToLines(Order order) =>
    [
        .. order.Lines.Select(line => new OrderLineDto
        {
            Sku = line.Sku,
            ProductName = line.ProductName,
            Quantity = line.Quantity,
            UnitPrice = ToDto(line.UnitPrice),
        }),
    ];

    private static MoneyDto ToDto(Money money) => new() { Amount = money.Amount, Currency = money.Currency };

    private static AddressDto ToDto(ShippingAddress address) => new()
    {
        RecipientName = address.RecipientName,
        Line1 = address.Line1,
        Line2 = address.Line2,
        City = address.City,
        PostalCode = address.PostalCode,
        CountryCode = address.CountryCode,
        Phone = address.Phone,
    };
}
