namespace ShopFlow.Contracts.Shipping.V1;

[MessageContract("ShipmentCreated", 1)]
public sealed record ShipmentCreated : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ShipmentId { get; init; }

    public required string CarrierCode { get; init; }

    public required string TrackingNumber { get; init; }

    /// <summary>Label path relative to the <c>labels</c> container.</summary>
    public required string LabelBlobPath { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

[MessageContract("ShipmentCreationFailed", 1)]
public sealed record ShipmentCreationFailed : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    /// <summary><c>address_undeliverable</c>, <c>carrier_unavailable</c>, <c>parcel_too_large</c>.</summary>
    public required string ReasonCode { get; init; }

    public string? ReasonDescription { get; init; }

    public required DateTimeOffset FailedAt { get; init; }
}

[MessageContract("ShipmentCancelled", 1)]
public sealed record ShipmentCancelled : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ShipmentId { get; init; }

    public required DateTimeOffset CancelledAt { get; init; }
}

/// <summary>The carrier picked the parcel up from the warehouse (carrier webhook).</summary>
[MessageContract("ShipmentDispatched", 1)]
public sealed record ShipmentDispatched : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ShipmentId { get; init; }

    public required string CarrierCode { get; init; }

    public required string TrackingNumber { get; init; }

    public required DateTimeOffset DispatchedAt { get; init; }

    public DateOnly? EstimatedDeliveryDate { get; init; }
}

[MessageContract("ShipmentDelivered", 1)]
public sealed record ShipmentDelivered : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ShipmentId { get; init; }

    public required DateTimeOffset DeliveredAt { get; init; }
}
