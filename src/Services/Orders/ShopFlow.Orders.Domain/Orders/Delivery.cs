using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Domain.Orders;

public enum DeliveryMethod
{
    Courier = 0,
    PickupPoint = 1,
}

public sealed record ShippingAddress(
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string CountryCode,
    string? Phone);

/// <summary>Delivery method. An address and a pickup point are mutually exclusive.</summary>
public sealed record Delivery
{
    private Delivery(DeliveryMethod method, ShippingAddress? address, string? pickupPointId)
    {
        Method = method;
        Address = address;
        PickupPointId = pickupPointId;
    }

    public DeliveryMethod Method { get; }

    public ShippingAddress? Address { get; }

    public string? PickupPointId { get; }

    public static Delivery Courier(ShippingAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new Delivery(DeliveryMethod.Courier, address, null);
    }

    public static Delivery PickupPoint(string pickupPointId)
    {
        if (string.IsNullOrWhiteSpace(pickupPointId))
        {
            throw new DomainException("delivery.pickup_point_required", "Pickup point delivery requires a pickup point.");
        }

        return new Delivery(DeliveryMethod.PickupPoint, null, pickupPointId);
    }

    /// <summary>Rehydration from the data store; validation happened at creation time.</summary>
    public static Delivery Restore(DeliveryMethod method, ShippingAddress? address, string? pickupPointId) =>
        new(method, address, pickupPointId);
}
