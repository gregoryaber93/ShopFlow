using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Domain.Orders;

/// <summary>
/// Order aggregate: the consistency boundary for lines, totals, status and history.
/// </summary>
/// <remarks>
/// Transition rules worth tracing:
/// <list type="bullet">
/// <item>Every transition is idempotent: repeating an operation in the target state succeeds without an event.
/// Messages and saga calls may arrive more than once.</item>
/// <item>A refusal returns a <see cref="Result"/> with a code. The application layer decides whether a refusal
/// means "retry later" (reordered messages) or "data error" (DLQ).</item>
/// </list>
/// </remarks>
public sealed class Order : AggregateRoot<Guid>
{
    public const int MaxLines = 50;

    private readonly List<OrderLine> _lines;
    private readonly List<OrderStatusChange> _history;

    private Order(
        Guid id,
        Guid customerId,
        string orderNumber,
        List<OrderLine> lines,
        Money total,
        Delivery delivery,
        string paymentMethodToken,
        OrderStatus status,
        DateTimeOffset submittedAt,
        List<OrderStatusChange> history)
        : base(id)
    {
        CustomerId = customerId;
        OrderNumber = orderNumber;
        _lines = lines;
        Total = total;
        Delivery = delivery;
        PaymentMethodToken = paymentMethodToken;
        Status = status;
        SubmittedAt = submittedAt;
        _history = history;
    }

    public Guid CustomerId { get; }

    public string OrderNumber { get; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public Money Total { get; }

    public Delivery Delivery { get; }

    /// <summary>
    /// One-time payment method token issued by the payment provider in the customer's browser.
    /// </summary>
    /// <remarks>
    /// WHY: card data never reaches ShopFlow (the PCI DSS scope stays with the provider).
    /// The token alone cannot charge a card without our credentials for the provider's API.
    /// </remarks>
    public string PaymentMethodToken { get; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset SubmittedAt { get; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? ShippedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public string? TrackingNumber { get; private set; }

    public string? RejectionReasonCode { get; private set; }

    public string? CancellationReason { get; private set; }

    public IReadOnlyList<OrderStatusChange> History => _history;

    public static Result<Order> Submit(
        Guid orderId,
        Guid customerId,
        string orderNumber,
        IReadOnlyList<OrderLine> lines,
        Delivery delivery,
        string paymentMethodToken,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(delivery);

        if (lines.Count == 0)
        {
            return OrderErrors.NoLines;
        }

        if (lines.Count > MaxLines)
        {
            return OrderErrors.TooManyLines;
        }

        if (lines.Select(line => line.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Count)
        {
            return OrderErrors.DuplicateSku;
        }

        var currency = lines[0].UnitPrice.Currency;
        if (lines.Any(line => line.UnitPrice.Currency != currency))
        {
            return OrderErrors.MixedCurrencies;
        }

        var total = lines.Aggregate(Money.Zero(currency), (sum, line) => sum + line.LineTotal);

        var order = new Order(
            orderId,
            customerId,
            orderNumber,
            [.. lines],
            total,
            delivery,
            paymentMethodToken,
            OrderStatus.Submitted,
            now,
            [new OrderStatusChange(OrderStatus.Submitted, now, null)]);

        order.Raise(new OrderSubmittedDomainEvent(orderId, now));
        return order;
    }

    /// <summary>The customer asks for cancellation. Possible only while the saga runs; the saga decides.</summary>
    public Result RequestCancellation(string requestedBy, string? reason, DateTimeOffset now)
    {
        if (Status == OrderStatus.CancellationRequested)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.Submitted)
        {
            return OrderErrors.CannotCancel;
        }

        CancellationReason = reason;
        TransitionTo(OrderStatus.CancellationRequested, now, $"requested by {requestedBy}");
        Raise(new OrderCancellationRequestedDomainEvent(Id, requestedBy, reason, now));
        return Result.Success();
    }

    /// <summary>The saga succeeded.</summary>
    public Result Confirm(string? trackingNumber, DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.Confirmed or OrderStatus.Shipped or OrderStatus.Completed:
                return Result.Success();

            case OrderStatus.CancellationRequested:
                // The saga receives OrderCancellationRequested as an external event and compensates the steps.
                return OrderErrors.CancellationPending;

            case OrderStatus.Rejected or OrderStatus.Cancelled:
                return OrderErrors.AlreadyClosed;
        }

        TrackingNumber = trackingNumber;
        ConfirmedAt = now;
        TransitionTo(OrderStatus.Confirmed, now, null);
        Raise(new OrderConfirmedDomainEvent(Id, now));
        return Result.Success();
    }

    /// <summary>The saga failed and compensated the completed steps.</summary>
    public Result Reject(string reasonCode, string? description, DateTimeOffset now)
    {
        if (Status == OrderStatus.Rejected)
        {
            return Result.Success();
        }

        if (Status is not (OrderStatus.Submitted or OrderStatus.CancellationRequested))
        {
            return OrderErrors.InvalidTransition;
        }

        RejectionReasonCode = reasonCode;
        ClosedAt = now;
        TransitionTo(OrderStatus.Rejected, now, description ?? reasonCode);
        Raise(new OrderRejectedDomainEvent(Id, reasonCode, description, now));
        return Result.Success();
    }

    /// <summary>The saga compensated the steps after the customer asked for cancellation.</summary>
    public Result CompleteCancellation(DateTimeOffset now)
    {
        if (Status == OrderStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.CancellationRequested)
        {
            return OrderErrors.InvalidTransition;
        }

        ClosedAt = now;
        TransitionTo(OrderStatus.Cancelled, now, CancellationReason);
        Raise(new OrderCancelledDomainEvent(Id, CancellationReason, now));
        return Result.Success();
    }

    /// <summary>The carrier picked up the parcel (ShipmentDispatched event).</summary>
    public Result MarkShipped(string trackingNumber, DateTimeOffset at)
    {
        switch (Status)
        {
            // A duplicate or an event that arrived after the delivery: the target state has already been reached.
            case OrderStatus.Shipped or OrderStatus.Completed:
                return Result.Success();

            case OrderStatus.Submitted or OrderStatus.CancellationRequested:
                return OrderErrors.NotConfirmedYet;

            case OrderStatus.Rejected or OrderStatus.Cancelled:
                return OrderErrors.AlreadyClosed;
        }

        TrackingNumber = trackingNumber;
        ShippedAt = at;
        TransitionTo(OrderStatus.Shipped, at, trackingNumber);
        Raise(new OrderShippedDomainEvent(Id, trackingNumber, at));
        return Result.Success();
    }

    /// <summary>The shipment was delivered (ShipmentDelivered event).</summary>
    /// <remarks>
    /// WHY: the transition is also allowed from <see cref="OrderStatus.Confirmed"/>. ShipmentDispatched and
    /// ShipmentDelivered are two different messages and may arrive in reverse order (e.g. the first one went into a
    /// retry). Requiring the Shipped state would block the delivery until the late "dispatched" arrives, which is
    /// meaningless after the delivery.
    /// </remarks>
    public Result MarkDelivered(DateTimeOffset at)
    {
        switch (Status)
        {
            case OrderStatus.Completed:
                return Result.Success();

            case OrderStatus.Submitted or OrderStatus.CancellationRequested:
                return OrderErrors.NotConfirmedYet;

            case OrderStatus.Rejected or OrderStatus.Cancelled:
                return OrderErrors.AlreadyClosed;
        }

        ClosedAt = at;
        TransitionTo(OrderStatus.Completed, at, null);
        Raise(new OrderCompletedDomainEvent(Id, at));
        return Result.Success();
    }

    public OrderState ToState() => new(
        Id,
        CustomerId,
        OrderNumber,
        _lines,
        Total,
        Delivery,
        PaymentMethodToken,
        Status,
        SubmittedAt,
        ConfirmedAt,
        ShippedAt,
        ClosedAt,
        TrackingNumber,
        RejectionReasonCode,
        CancellationReason,
        _history,
        ConcurrencyToken);

    public static Order Restore(OrderState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var order = new Order(
            state.OrderId,
            state.CustomerId,
            state.OrderNumber,
            [.. state.Lines],
            state.Total,
            state.Delivery,
            state.PaymentMethodToken,
            state.Status,
            state.SubmittedAt,
            [.. state.History])
        {
            ConfirmedAt = state.ConfirmedAt,
            ShippedAt = state.ShippedAt,
            ClosedAt = state.ClosedAt,
            TrackingNumber = state.TrackingNumber,
            RejectionReasonCode = state.RejectionReasonCode,
            CancellationReason = state.CancellationReason,
        };

        order.SetConcurrencyToken(state.ConcurrencyToken);
        return order;
    }

    private void TransitionTo(OrderStatus status, DateTimeOffset at, string? note)
    {
        Status = status;
        _history.Add(new OrderStatusChange(status, at, note));
    }
}
