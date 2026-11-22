using Microsoft.Extensions.Logging;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Domain;

namespace ShopFlow.Payments.Application.Webhooks;

/// <summary>
/// Event from the payment provider after signature verification.
/// </summary>
/// <param name="EventId">Event identifier assigned by the provider. The deduplication key.</param>
/// <param name="Type"><c>authorization.succeeded</c> or <c>authorization.failed</c>.</param>
/// <param name="OrderId">From the metadata we passed to the provider at authorization.</param>
/// <param name="PaymentId">From the metadata, as above.</param>
/// <param name="ProviderReference">Transaction identifier at the provider.</param>
/// <param name="DeclineCode">Decline code (for authorization.failed).</param>
/// <param name="AuthorizationExpiresAt">Until when the hold is valid (for authorization.succeeded).</param>
public sealed record PspWebhookEvent(
    string EventId,
    string Type,
    Guid OrderId,
    Guid PaymentId,
    string ProviderReference,
    string? DeclineCode,
    DateTimeOffset? AuthorizationExpiresAt);

public enum WebhookOutcome
{
    Processed = 0,
    Ignored = 1,

    /// <summary>The payment is not visible yet: we answer 5xx and the provider retries the webhook later.</summary>
    RetryLater = 2,
}

/// <summary>
/// 3-D Secure result delivered asynchronously by the payment provider.
/// </summary>
/// <remarks>
/// WHY: why the saga waits up to 15 minutes for the authorization. When the bank requires 3-D Secure, the answer to
/// "authorize" is "customer action required". The customer confirms the payment in the bank's app and the provider
/// tells us through a webhook. Only then do we publish PaymentAuthorized or PaymentDeclined, and the saga moves on.
/// <para>
/// Deduplication: the calling function sets the event identifier as the "incoming message" in the operation context,
/// so the same inbox as for Service Bus (a document in the order's partition) rejects a repeated webhook. Payment
/// providers retry webhooks aggressively, usually for several days.
/// </para>
/// </remarks>
public sealed partial class PspWebhookProcessor(
    IPaymentRepository payments,
    PaymentEventPublisher publisher,
    TimeProvider timeProvider,
    ILogger<PspWebhookProcessor> logger)
{
    public async Task<WebhookOutcome> ProcessAsync(PspWebhookEvent webhook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        var succeeded = webhook.Type switch
        {
            "authorization.succeeded" => true,
            "authorization.failed" => false,
            _ => (bool?)null,
        };

        if (succeeded is null)
        {
            // The provider also sends types we do not handle (e.g. settlement reports). We acknowledge them (200);
            // otherwise it would keep retrying them for days.
            LogIgnored(webhook.EventId, webhook.Type);
            return WebhookOutcome.Ignored;
        }

        var payment = await payments.GetAsync(webhook.OrderId, webhook.PaymentId, cancellationToken).ConfigureAwait(false);
        if (payment is null)
        {
            // The webhook overtook saving the payment after the authorization call. The provider will retry it shortly.
            return WebhookOutcome.RetryLater;
        }

        payment.CompleteAuthorization(
            new AuthorizationResult(
                succeeded.Value ? AuthorizationOutcome.Authorized : AuthorizationOutcome.Declined,
                webhook.ProviderReference,
                webhook.DeclineCode ?? (succeeded.Value ? null : "authentication_failed"),
                null,
                webhook.AuthorizationExpiresAt),
            timeProvider.GetUtcNow());

        publisher.Publish(payment);
        await payments.SaveAsync(payment, cancellationToken).ConfigureAwait(false);
        return WebhookOutcome.Processed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PSP webhook {EventId} of type {Type} ignored")]
    private partial void LogIgnored(string eventId, string type);
}
