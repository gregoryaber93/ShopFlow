using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Orders.V1;

/// <summary>
/// The customer placed an order. Starts the fulfilment saga.
/// </summary>
/// <remarks>
/// Version 1 assumes courier delivery to an address. Superseded by
/// <see cref="V2.OrderSubmitted"/>, which also allows pickup-point delivery. Published side by side
/// with version 2 until the end of the sunset period (feature flag <c>Orders.PublishOrderSubmittedV1</c>).
/// </remarks>
[MessageContract("OrderSubmitted", 1)]
public sealed record OrderSubmitted : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    /// <summary>Human-readable number, e.g. <c>SF-2026-000123</c>.</summary>
    public required string OrderNumber { get; init; }

    public required IReadOnlyList<OrderLineDto> Lines { get; init; }

    public required MoneyDto Total { get; init; }

    public required AddressDto ShippingAddress { get; init; }

    /// <summary>
    /// Payment method token issued by the payment provider in the browser.
    /// Card data never reaches ShopFlow.
    /// </summary>
    public required string PaymentMethodToken { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}

/// <summary>The saga succeeded: stock reserved, payment captured, shipment created.</summary>
[MessageContract("OrderConfirmed", 1)]
public sealed record OrderConfirmed : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required MoneyDto Total { get; init; }

    public required DateTimeOffset ConfirmedAt { get; init; }

    public string? TrackingNumber { get; init; }
}

/// <summary>The saga failed and every completed step has been compensated.</summary>
[MessageContract("OrderRejected", 1)]
public sealed record OrderRejected : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    /// <summary>One of the <see cref="OrderRejectionReasons"/> values.</summary>
    public required string ReasonCode { get; init; }

    public string? ReasonDescription { get; init; }

    public required DateTimeOffset RejectedAt { get; init; }
}

/// <summary>The customer or an operator asked for cancellation. The saga decides whether it is still possible.</summary>
[MessageContract("OrderCancellationRequested", 1)]
public sealed record OrderCancellationRequested : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    /// <summary><c>customer</c> or <c>backoffice</c>.</summary>
    public required string RequestedBy { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
}

[MessageContract("OrderCancelled", 1)]
public sealed record OrderCancelled : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset CancelledAt { get; init; }
}

/// <summary>The shipment was delivered; the order is closed.</summary>
[MessageContract("OrderCompleted", 1)]
public sealed record OrderCompleted : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// Order rejection reason codes.
/// </summary>
/// <remarks>
/// WHY: strings, not an enum. The set of reasons will grow, and adding an enum value is a breaking
/// change for every consumer with an older package (deserializing an unknown value throws).
/// A consumer simply treats an unknown string as "other reason".
/// </remarks>
public static class OrderRejectionReasons
{
    public const string OutOfStock = "out_of_stock";
    public const string PaymentDeclined = "payment_declined";
    public const string PaymentTimeout = "payment_timeout";
    public const string ShipmentUnavailable = "shipment_unavailable";
    public const string CancelledByCustomer = "cancelled_by_customer";
    public const string InternalError = "internal_error";
}
