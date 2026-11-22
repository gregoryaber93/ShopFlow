using System.Text.Json.Serialization;
using ShopFlow.Contracts.Common;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using OrdersV2 = ShopFlow.Contracts.Orders.V2;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.CustomerReadModels.Application;

/// <summary>
/// Denormalized view of one order from the customer's perspective: the header from Orders, the payment from Payments,
/// the shipment from Shipping. One document in the <c>customer-orders</c> container, in the customer's partition.
/// </summary>
/// <remarks>
/// WHY: the CQRS read side has no domain and no invariants. It is a data shape for specific screens ("my orders", "order
/// details"), so the view is a plain class with fields, and the logic is limited to how an event changes the fields.
/// <para>
/// WHY: the view stores facts (when it was confirmed, when the parcel was dispatched), and the status is derived from them.
/// Events come from three topics, so their order in the queue is not guaranteed: <c>ShipmentDispatched</c> may arrive before
/// <c>OrderConfirmed</c>. If the status were simply "the last event", a late confirmation would move the order back from
/// "shipped" to "confirmed". Facts do not depend on order, and there is one rule for deriving the status.
/// </para>
/// </remarks>
public sealed class CustomerOrderView
{
    public const string DocumentType = "customer-order";

    /// <summary>Version of the document shape. A shape change raises the version, and old documents are recomputed lazily on the next event or by a rebuild.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// How many recent message identifiers are remembered in the document.
    /// </summary>
    /// <remarks>
    /// An order has a dozen or so events in its whole life, so the limit practically never trims the list; it only protects the
    /// document from unbounded growth (e.g. after the archive has been replayed many times).
    /// </remarks>
    public const int AppliedMessagesLimit = 64;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("customerId")]
    public required string CustomerId { get; init; }

    [JsonPropertyName("docType")]
    public string DocType { get; init; } = DocumentType;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required Guid OrderId { get; init; }

    /// <summary>No number means <c>OrderSubmitted</c> has not arrived yet (see <see cref="HasHeader"/>).</summary>
    public string? OrderNumber { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public MoneyView? Total { get; set; }

    public List<OrderLineView> Lines { get; set; } = [];

    public DeliveryView? Delivery { get; set; }

    public string Status { get; set; } = CustomerOrderStatus.Processing;

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset? RejectedAt { get; set; }

    public string? RejectionReason { get; set; }

    public DateTimeOffset? CancellationRequestedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public PaymentView Payment { get; set; } = new();

    public InvoiceView? Invoice { get; set; }

    public ShipmentView? Shipment { get; set; }

    /// <summary>
    /// Inbox inside the document.
    /// </summary>
    /// <remarks>
    /// WHY: the view change and the record of the processed message are one write of one document, so they are atomic without
    /// a TransactionalBatch and without separate inbox documents. Most facts are idempotent by themselves ("set the confirmation
    /// date"), but refunds add up, and a refund counted twice is a wrong amount on the screen.
    /// </remarks>
    public List<Guid> AppliedMessageIds { get; set; } = [];

    /// <summary>Occurrence time of the latest applied event (for the projection lag metric).</summary>
    public DateTimeOffset? LastEventAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Document version in Cosmos DB. Used for optimistic concurrency and as the HTTP response ETag.</summary>
    [JsonPropertyName("_etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }

    [JsonIgnore]
    public bool HasHeader => OrderNumber is not null;

    /// <summary>
    /// View of an order whose <c>OrderSubmitted</c> has not arrived yet.
    /// </summary>
    /// <remarks>
    /// WHY: it is enough for a payment event to arrive before the submission event (different topics, different relays), and
    /// the projector can neither reject it nor wait. Every event carries <c>CustomerId</c> and <c>OrderId</c>, so we create the
    /// document without a header and complete it when the header arrives. The API does not show such documents.
    /// </remarks>
    public static CustomerOrderView Placeholder(Guid customerId, Guid orderId) => new()
    {
        Id = orderId.ToString("D"),
        CustomerId = customerId.ToString("D"),
        OrderId = orderId,
    };

    public bool HasApplied(Guid messageId) => AppliedMessageIds.Contains(messageId);

    public void MarkApplied(Guid messageId, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        AppliedMessageIds.Add(messageId);
        if (AppliedMessageIds.Count > AppliedMessagesLimit)
        {
            AppliedMessageIds.RemoveRange(0, AppliedMessageIds.Count - AppliedMessagesLimit);
        }

        LastEventAt = LastEventAt is { } last && last > occurredAt ? last : occurredAt;
        UpdatedAt = now;
        SchemaVersion = CurrentSchemaVersion;
        Status = CustomerOrderStatus.Derive(this);
    }

    // --- Orders -------------------------------------------------------------------------------------------

    public void Apply(OrdersV2.OrderSubmitted message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OrderNumber = message.OrderNumber;
        SubmittedAt = message.SubmittedAt;
        Total = MoneyView.From(message.Total);
        Lines = [.. message.Lines.Select(line => new OrderLineView(line.Sku, line.ProductName, line.Quantity, MoneyView.From(line.UnitPrice)))];
        Delivery = DeliveryView.From(message.Delivery);
    }

    public void Apply(OrdersV1.OrderConfirmed message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ConfirmedAt ??= message.ConfirmedAt;
    }

    public void Apply(OrdersV1.OrderRejected message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RejectedAt ??= message.RejectedAt;

        // The code only, without the technical description: the UI picks the customer-facing text based on the code.
        RejectionReason = message.ReasonCode;
    }

    public void Apply(OrdersV1.OrderCancellationRequested message)
    {
        ArgumentNullException.ThrowIfNull(message);
        CancellationRequestedAt ??= message.RequestedAt;
    }

    public void Apply(OrdersV1.OrderCancelled message)
    {
        ArgumentNullException.ThrowIfNull(message);
        CancelledAt ??= message.CancelledAt;
    }

    public void Apply(OrdersV1.OrderCompleted message)
    {
        ArgumentNullException.ThrowIfNull(message);
        CompletedAt ??= message.CompletedAt;
    }

    // --- Payments -----------------------------------------------------------------------------------------

    public void Apply(PaymentsV1.PaymentAuthorized message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Payment.AuthorizedAt ??= message.AuthorizedAt;
        Payment.Amount ??= MoneyView.From(message.Amount);
    }

    public void Apply(PaymentsV1.PaymentDeclined message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Payment.DeclinedAt ??= message.DeclinedAt;
    }

    public void Apply(PaymentsV1.PaymentCaptured message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Payment.CapturedAt ??= message.CapturedAt;
        Payment.Amount = MoneyView.From(message.Amount);
    }

    public void Apply(PaymentsV1.PaymentCaptureFailed message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Payment.CaptureFailedAt ??= message.FailedAt;
    }

    public void Apply(PaymentsV1.PaymentVoided message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Payment.VoidedAt ??= message.VoidedAt;
    }

    /// <remarks>
    /// The only fact that accumulates: an order can have several partial refunds. That is why deduplication in
    /// <see cref="AppliedMessageIds"/> is necessary here, not just good hygiene.
    /// </remarks>
    public void Apply(PaymentsV1.PaymentRefunded message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A refund of 0 is a released hold without capturing money; it does not change the refunded amount.
        if (message.Amount.Amount <= 0m)
        {
            return;
        }

        Payment.RefundedAmount = Payment.RefundedAmount is { } refunded
            ? refunded with { Amount = refunded.Amount + message.Amount.Amount }
            : MoneyView.From(message.Amount);
        Payment.LastRefundAt = message.RefundedAt;
    }

    public void Apply(PaymentsV1.InvoiceIssued message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // An invoice correction arrives as another InvoiceIssued; the view shows the latest one.
        if (Invoice is null || message.IssuedAt >= Invoice.IssuedAt)
        {
            Invoice = new InvoiceView(message.InvoiceNumber, message.BlobPath, message.IssuedAt);
        }
    }

    // --- Shipping -----------------------------------------------------------------------------------------

    public void Apply(ShippingV1.ShipmentCreated message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var shipment = ShipmentFor(message.ShipmentId);
        shipment.CarrierCode = message.CarrierCode;
        shipment.TrackingNumber = message.TrackingNumber;
        shipment.CreatedAt ??= message.CreatedAt;
    }

    public void Apply(ShippingV1.ShipmentCancelled message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ShipmentFor(message.ShipmentId).CancelledAt ??= message.CancelledAt;
    }

    public void Apply(ShippingV1.ShipmentDispatched message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var shipment = ShipmentFor(message.ShipmentId);
        shipment.CarrierCode = message.CarrierCode;
        shipment.TrackingNumber = message.TrackingNumber;
        shipment.DispatchedAt ??= message.DispatchedAt;
        shipment.EstimatedDeliveryDate = message.EstimatedDeliveryDate ?? shipment.EstimatedDeliveryDate;
    }

    public void Apply(ShippingV1.ShipmentDelivered message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ShipmentFor(message.ShipmentId).DeliveredAt ??= message.DeliveredAt;
    }

    private ShipmentView ShipmentFor(Guid shipmentId) => Shipment ??= new ShipmentView { ShipmentId = shipmentId };
}

public sealed record MoneyView(decimal Amount, string Currency)
{
    public static MoneyView From(MoneyDto money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return new MoneyView(money.Amount, money.Currency);
    }
}

public sealed record OrderLineView(string Sku, string ProductName, int Quantity, MoneyView UnitPrice);

/// <summary>Delivery in a screen-friendly shape: the method plus either an address or a pickup point.</summary>
public sealed record DeliveryView(string Method, string? RecipientName, string? Line1, string? Line2, string? City, string? PostalCode, string? CountryCode, string? PickupPointId)
{
    public static DeliveryView From(OrdersV2.DeliveryDto delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var address = delivery.Address;
        return new DeliveryView(
            delivery.Method == OrdersV2.DeliveryMethod.PickupPoint ? "pickupPoint" : "courier",
            address?.RecipientName,
            address?.Line1,
            address?.Line2,
            address?.City,
            address?.PostalCode,
            address?.CountryCode,
            delivery.PickupPointId);
    }
}

public sealed class PaymentView
{
    public MoneyView? Amount { get; set; }

    public DateTimeOffset? AuthorizedAt { get; set; }

    public DateTimeOffset? DeclinedAt { get; set; }

    public DateTimeOffset? CapturedAt { get; set; }

    public DateTimeOffset? CaptureFailedAt { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public MoneyView? RefundedAmount { get; set; }

    public DateTimeOffset? LastRefundAt { get; set; }
}

/// <param name="BlobPath">File path in the <c>invoices</c> container. Not returned by the API; used to issue the link.</param>
public sealed record InvoiceView(string Number, string BlobPath, DateTimeOffset IssuedAt);

public sealed class ShipmentView
{
    public required Guid ShipmentId { get; init; }

    public string? CarrierCode { get; set; }

    public string? TrackingNumber { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public DateOnly? EstimatedDeliveryDate { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>Order status as seen by the customer.</summary>
/// <remarks>
/// WHY: strings, not an enum, for the same reason as the codes in contracts: an older mobile app must not crash on a new
/// status, but show it as "in progress".
/// </remarks>
public static class CustomerOrderStatus
{
    public const string Processing = "processing";
    public const string Submitted = "submitted";
    public const string CancellationRequested = "cancellationRequested";
    public const string Confirmed = "confirmed";
    public const string Shipped = "shipped";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Delivered = "delivered";

    /// <summary>One precedence rule for facts, independent of the order in which they arrived.</summary>
    public static string Derive(CustomerOrderView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.CompletedAt is not null || view.Shipment?.DeliveredAt is not null)
        {
            return Delivered;
        }

        if (view.CancelledAt is not null)
        {
            return Cancelled;
        }

        if (view.RejectedAt is not null)
        {
            return Rejected;
        }

        if (view.Shipment?.DispatchedAt is not null)
        {
            return Shipped;
        }

        if (view.ConfirmedAt is not null)
        {
            return Confirmed;
        }

        if (view.CancellationRequestedAt is not null)
        {
            return CancellationRequested;
        }

        return view.HasHeader && view.Payment.AuthorizedAt is null && view.Shipment is null ? Submitted : Processing;
    }
}
