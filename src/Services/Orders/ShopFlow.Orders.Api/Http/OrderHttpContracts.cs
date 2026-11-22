using ShopFlow.Orders.Domain.Carts;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Api.Http;

// WHY: HTTP contracts are separate from the domain model and from message contracts. Each of these three
// boundaries evolves at a different pace: the public API has /v1 and /v2, events have their own versions,
// and the domain model changes with every refactoring.

public enum DeliveryMethodDto
{
    Courier,
    PickupPoint,
}

public sealed record AddressDto(
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string CountryCode,
    string? Phone)
{
    public ShippingAddress ToDomain() => new(RecipientName, Line1, Line2, City, PostalCode, CountryCode, Phone);

    public static AddressDto From(ShippingAddress address) =>
        new(address.RecipientName, address.Line1, address.Line2, address.City, address.PostalCode, address.CountryCode, address.Phone);
}

/// <summary>POST /v1/orders. Version being retired: courier delivery only.</summary>
public sealed record SubmitOrderRequestV1(AddressDto ShippingAddress, string PaymentMethodToken);

public sealed record DeliveryDto(DeliveryMethodDto Method, AddressDto? Address, string? PickupPointId);

/// <summary>POST /v2/orders. Courier or pickup-point delivery.</summary>
public sealed record SubmitOrderRequestV2(DeliveryDto Delivery, string PaymentMethodToken);

public sealed record OrderAcceptedResponse(Guid OrderId, string OrderNumber, OrderStatus Status);

public sealed record MoneyResponse(decimal Amount, string Currency)
{
    public static MoneyResponse From(Money money) => new(money.Amount, money.Currency);
}

public sealed record OrderLineResponse(string Sku, string ProductName, int Quantity, MoneyResponse UnitPrice, MoneyResponse LineTotal);

public sealed record StatusChangeResponse(OrderStatus Status, DateTimeOffset At, string? Note);

/// <summary>
/// Order in the API response (shared by v1 and v2).
/// </summary>
/// <remarks>
/// WHY: one response shape for both versions, because the change is additive. The <see cref="Delivery"/> field
/// was added in v2, while <see cref="ShippingAddress"/> stays for v1 clients and is populated for courier
/// deliveries. A v1 client ignores unknown fields, so it does not need a separate GET endpoint.
/// </remarks>
public sealed record OrderResponse(
    Guid OrderId,
    string OrderNumber,
    OrderStatus Status,
    IReadOnlyList<OrderLineResponse> Lines,
    MoneyResponse Total,
    DeliveryDto Delivery,
    AddressDto? ShippingAddress,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? ClosedAt,
    string? TrackingNumber,
    string? RejectionReasonCode,
    IReadOnlyList<StatusChangeResponse> History)
{
    public static OrderResponse From(Order order)
    {
        var address = order.Delivery.Address is { } value ? AddressDto.From(value) : null;

        return new OrderResponse(
            order.Id,
            order.OrderNumber,
            order.Status,
            [.. order.Lines.Select(line => new OrderLineResponse(
                line.Sku,
                line.ProductName,
                line.Quantity,
                MoneyResponse.From(line.UnitPrice),
                MoneyResponse.From(line.LineTotal)))],
            MoneyResponse.From(order.Total),
            new DeliveryDto(
                order.Delivery.Method == DeliveryMethod.Courier ? DeliveryMethodDto.Courier : DeliveryMethodDto.PickupPoint,
                address,
                order.Delivery.PickupPointId),
            address,
            order.SubmittedAt,
            order.ConfirmedAt,
            order.ShippedAt,
            order.ClosedAt,
            order.TrackingNumber,
            order.RejectionReasonCode,
            [.. order.History.Select(change => new StatusChangeResponse(change.Status, change.At, change.Note))]);
    }
}

public sealed record CancelOrderRequest(string? Reason);

public sealed record SetCartItemRequest(int Quantity);

public sealed record CartItemResponse(string Sku, int Quantity);

public sealed record CartResponse(IReadOnlyList<CartItemResponse> Items, DateTimeOffset? UpdatedAt)
{
    public static readonly CartResponse Empty = new([], null);

    public static CartResponse From(Cart cart) =>
        new([.. cart.Items.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new CartItemResponse(item.Key, item.Value))], cart.UpdatedAt);
}

public sealed record ConfirmOrderRequest(string? TrackingNumber);

public sealed record RejectOrderRequest(string ReasonCode, string? Description);
