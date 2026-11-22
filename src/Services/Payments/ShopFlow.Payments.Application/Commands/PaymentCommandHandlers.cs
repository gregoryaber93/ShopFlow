using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Domain;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Application.Commands;

// WHY: the common shape of every payment handler:
//   1. read the payment (partition = order),
//   2. perform the operation at the payment provider, if needed,
//   3. change the aggregate's state and save it together with the outbox and inbox.
// Steps 2 and 3 are not one transaction: you cannot atomically "charge a card and write to Cosmos DB".
// When step 2 succeeds and step 3 fails (e.g. 429 throttling), the message goes back to the queue and step 2 runs
// again. This is safe only thanks to the idempotency key the provider adapter sends with every call: the provider
// recognises the repeat and returns the result of the first call instead of charging the card a second time.

/// <summary>Saga step 2: authorization (placing a hold on funds).</summary>
public sealed class AuthorizePaymentHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    IPaymentFeatures features,
    PaymentEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<AuthorizePayment>
{
    public async Task HandleAsync(AuthorizePayment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // An older saga (before contracts 1.2) does not assign an identifier: we derive it deterministically so that a retry
        // of the same command hits the same payment and the same idempotency key at the provider.
        var paymentId = message.PaymentId ?? DeterministicGuid.Create($"payment/{message.OrderId:D}");

        var existing = await payments.GetAsync(message.OrderId, paymentId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (ReplyFor(existing) is { } reply)
            {
                outbox.Enqueue(reply);
            }

            // Save without a state change: the reply (outbox) and the inbox entry must go out in the same batch.
            await payments.SaveAsync(existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        var amount = new Money(message.Amount.Amount, message.Amount.Currency);
        var result = await gateway.AuthorizeAsync(
            new AuthorizationRequest(paymentId, message.OrderId, amount, message.PaymentMethodToken, await features.ForceThreeDSecureAsync().ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);

        var payment = Payment.Authorize(paymentId, message.OrderId, message.CustomerId, amount, result, timeProvider.GetUtcNow());
        publisher.Publish(payment);
        await payments.SaveAsync(payment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reply to a repeated command, consistent with the payment's current state.</summary>
    private static ShopFlow.Contracts.IMessage? ReplyFor(Payment payment) => payment.Status switch
    {
        PaymentStatus.Authorized or PaymentStatus.Captured or PaymentStatus.Refunded => Replies.Authorized(payment),
        PaymentStatus.Declined => Replies.Declined(payment, payment.DeclineCode ?? "declined", null),
        PaymentStatus.Voided => Replies.Declined(payment, "payment_voided", "The payment was voided before authorization."),

        // Waiting for 3-D Secure: the reply will come with the webhook.
        _ => null,
    };
}

/// <summary>Saga step 4: capturing the held funds.</summary>
public sealed class CapturePaymentHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    PaymentEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<CapturePayment>
{
    public async Task HandleAsync(CapturePayment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = timeProvider.GetUtcNow();

        // AZURE: a missing payment is most likely delayed replication (Session consistency in Cosmos DB works within one
        // client, and another function instance may have taken over the Service Bus session). The session guarantees command
        // order, so the authorization was definitely processed earlier.
        var payment = await payments.GetAsync(message.OrderId, message.PaymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new OutOfOrderMessageException($"Payment {message.PaymentId} is not visible yet.");

        switch (payment.Status)
        {
            case PaymentStatus.Authorized when payment.IsAuthorizationExpired(now):
                // No call to the provider: we know it would refuse. The saga releases the hold (no-op) and rejects the order.
                payment.MarkCaptureFailed("authorization_expired", now);
                break;

            case PaymentStatus.Authorized:
                var result = await gateway.CaptureAsync(payment.Id, payment.ProviderReference!, payment.Amount, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    payment.MarkCaptured(result.ProviderReference, now);
                }
                else
                {
                    payment.MarkCaptureFailed(result.FailureCode ?? "provider_rejected", now);
                }

                break;

            case PaymentStatus.Voided or PaymentStatus.Declined or PaymentStatus.Refunded:
                payment.MarkCaptureFailed("payment_not_capturable", now);
                break;

            case PaymentStatus.Pending:
                // The saga captures only after PaymentAuthorized, so "Pending" is a stale read from before the webhook.
                throw new OutOfOrderMessageException($"Payment {payment.Id} still looks pending; retrying with a fresh read.");
        }

        var published = publisher.Publish(payment);
        if (!published.Contains(nameof(PaymentCaptured)) && !published.Contains(nameof(PaymentCaptureFailed)))
        {
            outbox.Enqueue(Replies.Captured(payment));
        }

        await payments.SaveAsync(payment, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Compensation for step 2: "make sure the customer has no held or captured funds".
/// Always replies with PaymentVoided, because that is the event the saga waits for.
/// </summary>
public sealed class VoidPaymentHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    PaymentEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<VoidPayment>
{
    public async Task HandleAsync(VoidPayment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = timeProvider.GetUtcNow();

        var payment = await payments.GetAsync(message.OrderId, message.PaymentId, cancellationToken).ConfigureAwait(false)
            ?? Payment.VoidedTombstone(message.PaymentId, message.OrderId, message.CustomerId, now);

        await PaymentReversal.EnsureNotChargedAsync(payment, gateway, now, cancellationToken).ConfigureAwait(false);

        if (!publisher.Publish(payment).Contains(nameof(PaymentVoided)))
        {
            outbox.Enqueue(Replies.Voided(payment, now));
        }

        await payments.SaveAsync(payment, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Compensation for step 4. Always replies with PaymentRefunded; the amount says whether anything was refunded.</summary>
public sealed class RefundPaymentHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    PaymentEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<RefundPayment>
{
    public async Task HandleAsync(RefundPayment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = timeProvider.GetUtcNow();

        var payment = await payments.GetAsync(message.OrderId, message.PaymentId, cancellationToken).ConfigureAwait(false)
            ?? Payment.VoidedTombstone(message.PaymentId, message.OrderId, message.CustomerId, now);

        await PaymentReversal.EnsureNotChargedAsync(payment, gateway, now, cancellationToken).ConfigureAwait(false);

        if (!publisher.Publish(payment).Contains(nameof(PaymentRefunded)))
        {
            var refunded = payment.Status == PaymentStatus.Refunded ? payment.Amount : Money.Zero(payment.Amount.Currency);
            outbox.Enqueue(Replies.Refunded(payment, refunded, now));
        }

        await payments.SaveAsync(payment, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Logic shared by both compensations, state-tolerant: decides whether the provider needs a void, a refund, or nothing.
/// </summary>
internal static class PaymentReversal
{
    public static async Task EnsureNotChargedAsync(Payment payment, IPaymentGateway gateway, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (payment.Status)
        {
            case PaymentStatus.Pending or PaymentStatus.Authorized:
                if (payment.ProviderReference is { } reference)
                {
                    var voided = await gateway.VoidAsync(payment.Id, reference, cancellationToken).ConfigureAwait(false);
                    EnsureSucceeded(voided, payment, "void");
                }

                payment.MarkVoided(now);
                break;

            case PaymentStatus.Captured:
                var refunded = await gateway.RefundAsync(payment.Id, payment.ProviderReference!, payment.Amount, cancellationToken).ConfigureAwait(false);
                EnsureSucceeded(refunded, payment, "refund");
                payment.MarkRefunded(now);
                break;

                // Declined, Voided, Refunded: the customer was not charged, there is nothing to undo.
        }
    }

    private static void EnsureSucceeded(GatewayOperationResult result, Payment payment, string operation)
    {
        if (!result.Succeeded)
        {
            // The provider clearly refused the reversal (e.g. a chargeback in progress). Retrying changes nothing, and the
            // customer's money needs a human decision: DLQ, and after the timeout the saga escalates to an operator.
            throw new PermanentMessageException(
                DeadLetterReasons.BusinessRuleViolation,
                $"Provider refused to {operation} payment {payment.Id}: {result.FailureCode}");
        }
    }
}
