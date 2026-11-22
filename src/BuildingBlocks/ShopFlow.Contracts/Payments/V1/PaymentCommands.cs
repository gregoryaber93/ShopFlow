using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Payments.V1;

// AZURE: every payment command goes to a queue with sessions enabled (SessionId = OrderId).
// WHY:   authorization, capture and refund for one order must be processed in the order they were
//        sent. Without sessions two function instances could handle AuthorizePayment and VoidPayment
//        of the same order at the same time, and the result would depend on which one finished first.

/// <summary>Place a hold on the customer's payment method. Saga step 2.</summary>
[MessageContract("AuthorizePayment", 1)]
public sealed record AuthorizePayment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required MoneyDto Amount { get; init; }

    public required string PaymentMethodToken { get; init; }

    /// <summary>
    /// Payment identifier assigned by the sender (the saga). Optional field, added in package version 1.2.
    /// </summary>
    /// <remarks>
    /// WHY: the saga must be able to send <see cref="VoidPayment"/> even when no reply to the authorization
    /// arrived (timeout), and VoidPayment requires the payment identifier. When the saga assigns the
    /// identifier, it knows it from the start. The change is additive (a new optional field), so it does
    /// not need a new contract version: an older Payments ignores the field and assigns the identifier itself.
    /// </remarks>
    public Guid? PaymentId { get; init; }
}

/// <summary>Capture the previously held amount. Saga step 4.</summary>
[MessageContract("CapturePayment", 1)]
public sealed record CapturePayment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required MoneyDto Amount { get; init; }
}

/// <summary>Release the hold without capturing funds. Compensation for step 2.</summary>
[MessageContract("VoidPayment", 1)]
public sealed record VoidPayment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Refund captured funds. Compensation for step 4.</summary>
[MessageContract("RefundPayment", 1)]
public sealed record RefundPayment : ICommand, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required MoneyDto Amount { get; init; }

    public required string Reason { get; init; }
}
