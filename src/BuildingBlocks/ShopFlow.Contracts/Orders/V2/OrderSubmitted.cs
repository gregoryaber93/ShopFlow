using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Orders.V2;

/// <summary>
/// The customer placed an order. Starts the fulfilment saga.
/// </summary>
/// <remarks>
/// Breaking change compared with version 1: the mandatory <c>ShippingAddress</c> field was replaced
/// by a <see cref="Delivery"/> object in which the address is optional (pickup-point delivery has none).
/// A version 1 consumer could not handle an order without an address, so the change cannot be
/// introduced additively.
/// <para>
/// WHY: as long as version 1 is published side by side, pickup-point delivery stays disabled by the
/// <c>Orders.PickupPointDelivery</c> flag. The publisher must be able to express every order in both
/// versions, and a pickup-point order cannot be written as version 1.
/// </para>
/// </remarks>
[MessageContract("OrderSubmitted", 2)]
public sealed record OrderSubmitted : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required IReadOnlyList<OrderLineDto> Lines { get; init; }

    public required MoneyDto Total { get; init; }

    public required DeliveryDto Delivery { get; init; }

    public required string PaymentMethodToken { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}

public sealed record DeliveryDto
{
    public required DeliveryMethod Method { get; init; }

    /// <summary>Required for <see cref="DeliveryMethod.Courier"/>.</summary>
    public AddressDto? Address { get; init; }

    /// <summary>Required for <see cref="DeliveryMethod.PickupPoint"/>.</summary>
    public string? PickupPointId { get; init; }
}

/// <remarks>
/// An enum, because the set is closed: every new delivery method requires changes in Shipping anyway,
/// so adding a value means a new contract version.
/// </remarks>
public enum DeliveryMethod
{
    Courier = 0,
    PickupPoint = 1,
}
