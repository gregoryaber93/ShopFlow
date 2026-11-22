using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.Messaging.Outbox;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Inventory.Application;

/// <summary>Inventory domain events → integration events in the outbox (the messaging.OutboxMessages table).</summary>
public sealed class InventoryEventPublisher(IOutbox outbox)
{
    /// <returns><c>true</c> if the reservation changed state (had events).</returns>
    public bool Publish(Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var events = reservation.DequeueDomainEvents();

        foreach (var domainEvent in events)
        {
            switch (domainEvent)
            {
                case StockReservedDomainEvent:
                    outbox.Enqueue(Reserved(reservation));
                    break;

                case StockReleasedDomainEvent released:
                    outbox.Enqueue(Released(reservation, released.OccurredAt));
                    break;

                case StockLevelLowDomainEvent low:
                    outbox.Enqueue(new StockLevelLow
                    {
                        Sku = low.Sku,
                        WarehouseCode = low.WarehouseCode,
                        Available = low.Available,
                        ReorderThreshold = low.ReorderThreshold,
                        DetectedAt = low.OccurredAt,
                    });
                    break;

                case ReservationCommittedDomainEvent:
                    // An internal state change; nobody outside Inventory needs this information.
                    break;

                default:
                    throw new InvalidOperationException($"Domain event {domainEvent.GetType().Name} has no integration mapping.");
            }
        }

        return events.Count > 0;
    }

    public void Publish(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        foreach (var domainEvent in product.DequeueDomainEvents())
        {
            if (domainEvent is ProductPriceChangedDomainEvent changed)
            {
                outbox.Enqueue(new ProductPriceChanged
                {
                    Sku = changed.Sku,
                    OldPrice = ToDto(changed.OldPrice),
                    NewPrice = ToDto(changed.NewPrice),
                    EffectiveFrom = changed.OccurredAt,
                });
            }
        }
    }

    public static StockReserved Reserved(Reservation reservation) => new()
    {
        OrderId = reservation.OrderId,
        CustomerId = reservation.CustomerId,
        ReservationId = reservation.Id,
        WarehouseCode = reservation.WarehouseCode!,
        Lines = [.. reservation.Lines.Select(line => new StockLineDto { Sku = line.Sku, Quantity = line.Quantity })],
        ReservedAt = reservation.CreatedAt,
        ExpiresAt = reservation.ExpiresAt,
    };

    public static StockReleased Released(Reservation reservation, DateTimeOffset at) => new()
    {
        OrderId = reservation.OrderId,
        CustomerId = reservation.CustomerId,
        ReservationId = reservation.Id,
        ReleasedAt = at,
    };

    private static MoneyDto ToDto(Money money) => new() { Amount = money.Amount, Currency = money.Currency };
}
