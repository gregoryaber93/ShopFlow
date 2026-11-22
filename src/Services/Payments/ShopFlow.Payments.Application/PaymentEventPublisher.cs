using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Payments.Domain;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Application;

/// <summary>Payment domain events → integration events in the outbox (outbox documents in the order's partition).</summary>
public sealed class PaymentEventPublisher(IOutbox outbox)
{
    /// <returns>Names of the published contracts; the command handler checks whether a reply for the saga is among them.</returns>
    public IReadOnlySet<string> Publish(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var published = new HashSet<string>(StringComparer.Ordinal);

        foreach (var domainEvent in payment.DequeueDomainEvents())
        {
            var message = domainEvent switch
            {
                PaymentAuthorizedDomainEvent => Replies.Authorized(payment),
                PaymentDeclinedDomainEvent declined => Replies.Declined(payment, declined.DeclineCode, declined.Reason),
                PaymentCapturedDomainEvent => Replies.Captured(payment),
                PaymentCaptureFailedDomainEvent failed => Replies.CaptureFailed(payment, failed.ReasonCode, failed.OccurredAt),
                PaymentVoidedDomainEvent voided => Replies.Voided(payment, voided.OccurredAt),
                PaymentRefundedDomainEvent refunded => Replies.Refunded(payment, refunded.Amount, refunded.OccurredAt),
                InvoiceIssuedDomainEvent issued => (ShopFlow.Contracts.IMessage)new InvoiceIssued
                {
                    OrderId = payment.OrderId,
                    CustomerId = payment.CustomerId,
                    InvoiceNumber = payment.Invoice!.Number,
                    Total = ToDto(payment.Amount),
                    BlobPath = payment.Invoice.BlobPath,
                    IssuedAt = issued.OccurredAt,
                },
                _ => throw new InvalidOperationException($"Domain event {domainEvent.GetType().Name} has no integration mapping."),
            };

            outbox.Enqueue(message);
            published.Add(message.GetType().Name);
        }

        return published;
    }

    internal static MoneyDto ToDto(Money money) => new() { Amount = money.Amount, Currency = money.Currency };
}

/// <summary>Replies for the saga, built even when the payment state did not change (repeated command).</summary>
public static class Replies
{
    public static PaymentAuthorized Authorized(Payment payment) => new()
    {
        OrderId = payment.OrderId,
        CustomerId = payment.CustomerId,
        PaymentId = payment.Id,
        Amount = PaymentEventPublisher.ToDto(payment.Amount),
        ProviderReference = payment.ProviderReference ?? string.Empty,
        AuthorizedAt = payment.UpdatedAt,
        AuthorizationExpiresAt = payment.AuthorizationExpiresAt ?? payment.UpdatedAt.AddDays(7),
    };

    public static PaymentDeclined Declined(Payment payment, string declineCode, string? reason) => new()
    {
        OrderId = payment.OrderId,
        CustomerId = payment.CustomerId,
        PaymentId = payment.Id,
        DeclineCode = declineCode,
        DeclineReason = reason,
        DeclinedAt = payment.UpdatedAt,
    };

    public static PaymentCaptured Captured(Payment payment) => new()
    {
        OrderId = payment.OrderId,
        CustomerId = payment.CustomerId,
        PaymentId = payment.Id,
        Amount = PaymentEventPublisher.ToDto(payment.Amount),
        ProviderReference = payment.ProviderReference ?? string.Empty,
        CapturedAt = payment.UpdatedAt,
    };

    public static PaymentCaptureFailed CaptureFailed(Payment payment, string reasonCode, DateTimeOffset at) => new()
    {
        OrderId = payment.OrderId,
        CustomerId = payment.CustomerId,
        PaymentId = payment.Id,
        ReasonCode = reasonCode,
        FailedAt = at,
    };

    public static PaymentVoided Voided(Payment payment, DateTimeOffset at) => new()
    {
        OrderId = payment.OrderId,
        CustomerId = payment.CustomerId,
        PaymentId = payment.Id,
        VoidedAt = at,
    };

    /// <remarks>
    /// WHY: an <paramref name="amount"/> of zero means "the customer was not charged, the hold was released". After the
    /// capture step the saga compensates the payment with RefundPayment without knowing whether the capture happened, and
    /// waits for the PaymentRefunded reply. The amount tells what actually happened. Notifications sends no refund e-mail
    /// for a zero amount.
    /// </remarks>
    public static PaymentRefunded Refunded(Payment payment, Money amount, DateTimeOffset at) => new()
    {
        OrderId = payment.OrderId,
        CustomerId = payment.CustomerId,
        PaymentId = payment.Id,
        Amount = PaymentEventPublisher.ToDto(amount),
        RefundedAt = at,
    };
}
