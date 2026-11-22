using ShopFlow.CustomerReadModels.Application;

namespace ShopFlow.CustomerReadModels.Api;

// WHY: dedicated response types instead of serializing CustomerOrderView. The view contains internal fields (the invoice
// blob path, the list of applied messages, the ETag as a field), and the API contract must stay stable regardless of how
// the shape of the Cosmos DB document changes.

public sealed record MoneyResponse(decimal Amount, string Currency)
{
    public static MoneyResponse From(MoneyView money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return new MoneyResponse(money.Amount, money.Currency);
    }
}

public sealed record OrderSummaryResponse(Guid OrderId, string OrderNumber, string Status, DateTimeOffset SubmittedAt, MoneyResponse Total, int ItemCount)
{
    public static OrderSummaryResponse From(CustomerOrderSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new OrderSummaryResponse(summary.OrderId, summary.OrderNumber, summary.Status, summary.SubmittedAt, MoneyResponse.From(summary.Total), summary.ItemCount);
    }
}

/// <param name="NextPageToken">Pass it as <c>pageToken</c> to get the next page; <c>null</c> on the last page.</param>
public sealed record OrderListResponse(IReadOnlyList<OrderSummaryResponse> Items, string? NextPageToken);

public sealed record OrderLineResponse(string Sku, string ProductName, int Quantity, MoneyResponse UnitPrice);

public sealed record DeliveryResponse(string Method, string? RecipientName, string? Line1, string? Line2, string? City, string? PostalCode, string? CountryCode, string? PickupPointId);

/// <param name="Status"><c>pending</c>, <c>authorized</c>, <c>captured</c>, <c>declined</c>, <c>voided</c> or <c>refunded</c>.</param>
public sealed record PaymentResponse(string Status, MoneyResponse? Amount, MoneyResponse? RefundedAmount);

/// <param name="DownloadPath">Endpoint that issues a short-lived link to the file.</param>
public sealed record InvoiceResponse(string Number, DateTimeOffset IssuedAt, string DownloadPath);

public sealed record ShipmentResponse(string? CarrierCode, string? TrackingNumber, DateOnly? EstimatedDeliveryDate, DateTimeOffset? DispatchedAt, DateTimeOffset? DeliveredAt);

public sealed record OrderTimelineResponse(
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? RejectedAt,
    string? RejectionReason,
    DateTimeOffset? CancellationRequestedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? CompletedAt);

public sealed record OrderDetailsResponse(
    Guid OrderId,
    string OrderNumber,
    string Status,
    MoneyResponse? Total,
    IReadOnlyList<OrderLineResponse> Lines,
    DeliveryResponse? Delivery,
    PaymentResponse Payment,
    InvoiceResponse? Invoice,
    ShipmentResponse? Shipment,
    OrderTimelineResponse Timeline,
    DateTimeOffset UpdatedAt)
{
    public static OrderDetailsResponse From(CustomerOrderView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var delivery = view.Delivery is { } d
            ? new DeliveryResponse(d.Method, d.RecipientName, d.Line1, d.Line2, d.City, d.PostalCode, d.CountryCode, d.PickupPointId)
            : null;

        var shipment = view.Shipment is { CancelledAt: null } s
            ? new ShipmentResponse(s.CarrierCode, s.TrackingNumber, s.EstimatedDeliveryDate, s.DispatchedAt, s.DeliveredAt)
            : null;

        return new OrderDetailsResponse(
            view.OrderId,
            view.OrderNumber!,
            view.Status,
            view.Total is null ? null : MoneyResponse.From(view.Total),
            [.. view.Lines.Select(line => new OrderLineResponse(line.Sku, line.ProductName, line.Quantity, MoneyResponse.From(line.UnitPrice)))],
            delivery,
            PaymentFrom(view.Payment),
            view.Invoice is { } invoice ? new InvoiceResponse(invoice.Number, invoice.IssuedAt, $"/v1/me/orders/{view.OrderId:D}/invoice") : null,
            shipment,
            new OrderTimelineResponse(view.SubmittedAt, view.ConfirmedAt, view.RejectedAt, view.RejectionReason, view.CancellationRequestedAt, view.CancelledAt, view.CompletedAt),
            view.UpdatedAt);
    }

    private static PaymentResponse PaymentFrom(PaymentView payment)
    {
        var status = payment switch
        {
            { RefundedAmount: not null } => "refunded",
            { CapturedAt: not null } => "captured",
            { VoidedAt: not null } => "voided",
            { DeclinedAt: not null } => "declined",
            { AuthorizedAt: not null } => "authorized",
            _ => "pending",
        };

        return new PaymentResponse(
            status,
            payment.Amount is null ? null : MoneyResponse.From(payment.Amount),
            payment.RefundedAmount is null ? null : MoneyResponse.From(payment.RefundedAmount));
    }
}

public sealed record InvoiceLinkResponse(string InvoiceNumber, Uri Url, DateTimeOffset ExpiresAt);
