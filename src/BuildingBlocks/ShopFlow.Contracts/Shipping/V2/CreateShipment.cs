using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Shipping.V2;

/// <summary>
/// Order a shipment: courier delivery or pickup-point delivery. Saga step 3.
/// </summary>
/// <remarks>
/// Breaking change compared with version 1: the required <c>Destination</c> field (address) was
/// replaced by a <see cref="ShipmentDestinationDto"/> object in which the address is optional.
/// <para>
/// WHY: the saga sends the oldest version able to express a given command: v1 for courier deliveries,
/// v2 only for pickup points. Shipping with v2 support can therefore be deployed at any time before
/// the <c>Orders.PickupPointDelivery</c> flag is switched on, and courier orders do not depend on that
/// deployment. If the saga switched to v2 right away, an older Shipping would dead-letter every
/// shipment with the UnhandledMessageType reason.
/// </para>
/// </remarks>
[MessageContract("CreateShipment", 2)]
public sealed record CreateShipment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required Guid ShipmentId { get; init; }

    public required string WarehouseCode { get; init; }

    public required ShipmentDestinationDto Destination { get; init; }

    public required IReadOnlyList<StockLineDto> Lines { get; init; }
}

public sealed record ShipmentDestinationDto
{
    public required ShipmentDeliveryMethod Method { get; init; }

    /// <summary>Required for <see cref="ShipmentDeliveryMethod.Courier"/>.</summary>
    public AddressDto? Address { get; init; }

    /// <summary>Required for <see cref="ShipmentDeliveryMethod.PickupPoint"/>.</summary>
    public string? PickupPointId { get; init; }
}

public enum ShipmentDeliveryMethod
{
    Courier = 0,
    PickupPoint = 1,
}
