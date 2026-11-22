using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Shipping.Domain;

public enum ShipmentStatus
{
    Created = 0,
    Cancelled = 1,
    Dispatched = 2,
    Delivered = 3,
}

/// <summary>Delivery destination: an address (courier) or a pickup point.</summary>
public sealed record ShipmentDestination(
    bool IsPickupPoint,
    string? PickupPointId,
    string? RecipientName,
    string? Line1,
    string? Line2,
    string? City,
    string? PostalCode,
    string? CountryCode,
    string? Phone);

public sealed record ShipmentCreatedDomainEvent(Guid ShipmentId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ShipmentCancelledDomainEvent(Guid ShipmentId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ShipmentDispatchedDomainEvent(Guid ShipmentId, DateOnly? EstimatedDeliveryDate, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ShipmentDeliveredDomainEvent(Guid ShipmentId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Shipment of one order ("one warehouse = one parcel" policy, see Reservation in Inventory).</summary>
public sealed class Shipment : AggregateRoot<Guid>
{
    private Shipment(Guid id, Guid orderId, Guid customerId)
        : base(id)
    {
        OrderId = orderId;
        CustomerId = customerId;
    }

    public Guid OrderId { get; }

    public Guid CustomerId { get; }

    public string OrderNumber { get; private set; } = string.Empty;

    public string WarehouseCode { get; private set; } = string.Empty;

    public ShipmentDestination? Destination { get; private set; }

    public string? CarrierCode { get; private set; }

    public string? TrackingNumber { get; private set; }

    public string? LabelBlobPath { get; private set; }

    public ShipmentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateOnly? EstimatedDeliveryDate { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public static Shipment Create(
        Guid shipmentId,
        Guid orderId,
        Guid customerId,
        string orderNumber,
        string warehouseCode,
        ShipmentDestination destination,
        string carrierCode,
        string trackingNumber,
        string labelBlobPath,
        DateTimeOffset now)
    {
        var shipment = new Shipment(shipmentId, orderId, customerId)
        {
            OrderNumber = orderNumber,
            WarehouseCode = warehouseCode,
            Destination = destination,
            CarrierCode = carrierCode,
            TrackingNumber = trackingNumber,
            LabelBlobPath = labelBlobPath,
            Status = ShipmentStatus.Created,
            CreatedAt = now,
        };

        shipment.Raise(new ShipmentCreatedDomainEvent(shipmentId, now));
        return shipment;
    }

    /// <summary>The cancellation arrived before creation (queue without sessions); a later creation will be rejected.</summary>
    /// <remarks>The same pattern as Reservation.Tombstone in Inventory.</remarks>
    public static Shipment CancelledTombstone(Guid shipmentId, Guid orderId, Guid customerId, DateTimeOffset now)
    {
        var shipment = new Shipment(shipmentId, orderId, customerId) { Status = ShipmentStatus.Cancelled, CreatedAt = now, ClosedAt = now };
        shipment.Raise(new ShipmentCancelledDomainEvent(shipmentId, now));
        return shipment;
    }

    public Result Cancel(DateTimeOffset now)
    {
        switch (Status)
        {
            case ShipmentStatus.Cancelled:
                return Result.Success();

            case ShipmentStatus.Dispatched or ShipmentStatus.Delivered:
                return Error.Conflict("shipment.already_dispatched", "The parcel has already been picked up by the carrier.");
        }

        Status = ShipmentStatus.Cancelled;
        ClosedAt = now;
        Raise(new ShipmentCancelledDomainEvent(Id, now));
        return Result.Success();
    }

    public Result MarkDispatched(DateTimeOffset at, DateOnly? estimatedDeliveryDate)
    {
        switch (Status)
        {
            case ShipmentStatus.Dispatched or ShipmentStatus.Delivered:
                return Result.Success();

            case ShipmentStatus.Cancelled:
                return Error.Conflict("shipment.cancelled", "A cancelled shipment was picked up by the carrier.");
        }

        Status = ShipmentStatus.Dispatched;
        DispatchedAt = at;
        EstimatedDeliveryDate = estimatedDeliveryDate;
        Raise(new ShipmentDispatchedDomainEvent(Id, estimatedDeliveryDate, at));
        return Result.Success();
    }

    /// <remarks>Delivery also from the Created state: the "dispatched" webhook may have arrived later or not at all.</remarks>
    public Result MarkDelivered(DateTimeOffset at)
    {
        switch (Status)
        {
            case ShipmentStatus.Delivered:
                return Result.Success();

            case ShipmentStatus.Cancelled:
                return Error.Conflict("shipment.cancelled", "A cancelled shipment was delivered.");
        }

        Status = ShipmentStatus.Delivered;
        ClosedAt = at;
        Raise(new ShipmentDeliveredDomainEvent(Id, at));
        return Result.Success();
    }

    public static Shipment Restore(
        Guid id,
        Guid orderId,
        Guid customerId,
        string orderNumber,
        string warehouseCode,
        ShipmentDestination? destination,
        string? carrierCode,
        string? trackingNumber,
        string? labelBlobPath,
        ShipmentStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? dispatchedAt,
        DateOnly? estimatedDeliveryDate,
        DateTimeOffset? closedAt,
        string? concurrencyToken)
    {
        var shipment = new Shipment(id, orderId, customerId)
        {
            OrderNumber = orderNumber,
            WarehouseCode = warehouseCode,
            Destination = destination,
            CarrierCode = carrierCode,
            TrackingNumber = trackingNumber,
            LabelBlobPath = labelBlobPath,
            Status = status,
            CreatedAt = createdAt,
            DispatchedAt = dispatchedAt,
            EstimatedDeliveryDate = estimatedDeliveryDate,
            ClosedAt = closedAt,
        };

        shipment.SetConcurrencyToken(concurrencyToken);
        return shipment;
    }
}
