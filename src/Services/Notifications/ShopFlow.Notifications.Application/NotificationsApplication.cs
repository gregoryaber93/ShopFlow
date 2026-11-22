using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Messaging;

namespace ShopFlow.Notifications.Application;

// WHY: Notifications has no Domain project. There is no aggregate and no business invariants here, only a reaction to
// other contexts' events: find the recipient, compose the content, send, record a trace. A domain layer would be empty ceremony.

public enum NotificationChannel
{
    Email,
    Sms,
}

public enum NotificationStatus
{
    /// <summary>Send reserved, outcome unknown (in progress, or the previous attempt was interrupted half-way).</summary>
    Pending,

    /// <summary>The provider accepted the message for sending.</summary>
    Sent,

    /// <summary>Deliberately skipped, e.g. the customer deleted their account or gave no phone number.</summary>
    Skipped,

    /// <summary>The provider rejected the message for a permanent reason (e.g. an invalid address).</summary>
    Rejected,
}

/// <summary>Template names. They are part of the notification identifier and go into the send history.</summary>
public static class TemplateNames
{
    public const string OrderConfirmed = "order-confirmed";
    public const string OrderRejected = "order-rejected";
    public const string OrderCancelled = "order-cancelled";
    public const string RefundIssued = "refund-issued";
    public const string InvoiceIssued = "invoice-issued";
    public const string ShipmentDispatched = "shipment-dispatched";
    public const string ShipmentDelivered = "shipment-delivered";
    public const string StockLevelLow = "stock-level-low";
}

/// <summary>Customer contact details read at send time.</summary>
/// <remarks>
/// WHY: events carry only <c>CustomerId</c>, never an e-mail address. An event lives in subscriptions, the DLQ and the
/// Event Hubs archive, so personal data in its payload would spread across the whole system and could not be deleted on
/// the customer's request. The address is fetched from the identity directory only at send time.
/// </remarks>
public sealed record CustomerContact(Guid CustomerId, string? Email, string? DisplayName, string? MobilePhone, string? PreferredLanguage);

public interface ICustomerDirectory
{
    /// <returns><c>null</c> when the account no longer exists (e.g. deleted on the customer's request).</returns>
    Task<CustomerContact?> FindAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Recipient of a specific send. <see cref="Language"/> is <c>pl</c> or <c>en</c>.</summary>
public sealed record Recipient(string Address, string? DisplayName, string Language);

public abstract record NotificationContent;

public sealed record EmailContent(string Subject, string Html, string PlainText) : NotificationContent;

public sealed record SmsContent(string Text) : NotificationContent;

/// <param name="OperationId">Deterministic identifier of the send, the same on every attempt.</param>
/// <param name="IsRetry">The previous attempt may have reached the provider; the adapter should check before sending.</param>
public sealed record OutgoingEmail(Guid OperationId, string To, string? DisplayName, EmailContent Content, bool IsRetry);

public sealed record OutgoingSms(Guid OperationId, string To, SmsContent Content, bool IsRetry);

public sealed record DeliveryResult(bool Accepted, string? ProviderMessageId, string? FailureCode)
{
    public static DeliveryResult Sent(string providerMessageId) => new(true, providerMessageId, null);

    public static DeliveryResult Rejected(string failureCode) => new(false, null, failureCode);
}

/// <summary>
/// Sends an e-mail. The adapter reports a transient error (throttling, timeout) with an exception and returns a permanent
/// rejection as <see cref="DeliveryResult.Rejected"/>.
/// </summary>
public interface IEmailSender
{
    Task<DeliveryResult> SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

public interface ISmsSender
{
    Task<DeliveryResult> SendAsync(OutgoingSms sms, CancellationToken cancellationToken);
}

public interface IInvoiceLinks
{
    TimeSpan Lifetime { get; }

    Task<Uri> CreateAsync(string blobPath, CancellationToken cancellationToken);
}

public static class NotificationFeatureFlags
{
    /// <summary>Flag with the Microsoft.Targeting filter (a percentage of customers and a list of testers).</summary>
    public const string ShipmentSms = "Notifications.ShipmentSms";
}

public interface INotificationFeatures
{
    /// <summary>SMS when a parcel is dispatched, rolled out gradually to a share of customers.</summary>
    Task<bool> IsShipmentSmsEnabledAsync(Guid customerId);
}

/// <summary>One notification to send, before we know the recipient.</summary>
/// <param name="NotificationId">Idempotency key of the send and identifier of the history entry.</param>
/// <param name="PartitionKey">Customer identifier or <see cref="OperationsPartition"/>.</param>
public sealed record NotificationDraft(
    Guid NotificationId,
    string PartitionKey,
    string Template,
    NotificationChannel Channel,
    Guid? OrderId,
    Guid SourceMessageId)
{
    /// <summary>Synthetic history partition for operations team alerts (they do not concern any customer).</summary>
    public const string OperationsPartition = "operations";

    /// <summary>Customer notification: one per message, template and channel.</summary>
    /// <remarks>
    /// WHY: the identifier is derived from the message identifier. Redelivering the same message (retry, DLQ replay) gives
    /// the same identifier, so it lands on the existing history entry. The channel is part of the key, because one event can
    /// produce both an e-mail and an SMS.
    /// </remarks>
    public static NotificationDraft ForCustomer(MessageContext context, string template, NotificationChannel channel, Guid customerId, Guid orderId)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
            DeterministicGuid.Create($"notification/{template}/{channel}/{context.MessageId:D}"),
            customerId.ToString("D"),
            template,
            channel,
            orderId,
            context.MessageId);
    }

    /// <summary>Operations alert: one per business key, regardless of the number of events.</summary>
    /// <remarks>
    /// WHY: here the idempotency key is not the message identifier but the business meaning (e.g. "low stock of SKU X in
    /// warehouse Y on day Z"). Two different events with the same meaning give the same identifier, so the second alert is
    /// treated as a duplicate. Deduplication and rate limiting are the same mechanism.
    /// </remarks>
    public static NotificationDraft ForOperations(MessageContext context, string template, string businessKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
            DeterministicGuid.Create($"notification/{template}/{businessKey}"),
            OperationsPartition,
            template,
            NotificationChannel.Email,
            null,
            context.MessageId);
    }
}

/// <summary>Send reservation in the history.</summary>
/// <param name="Attempt">
/// Attempt number. Greater than 1 means the previous attempt reserved the send (so it was right before or during the
/// provider call) but did not record an outcome.
/// </param>
/// <param name="ETag">Entry version; completing the send overwrites it conditionally.</param>
public sealed record NotificationClaim(NotificationDraft Draft, int Attempt, DateTimeOffset ClaimedAt, string ETag)
{
    public bool IsRetry => Attempt > 1;
}

/// <param name="MaskedRecipient">Address with the local part masked. The full address is not stored.</param>
public sealed record NotificationCompletion(NotificationStatus Status, string? MaskedRecipient, string? ProviderMessageId, string? Reason);

/// <summary>
/// Send history that is also the idempotency mechanism.
/// </summary>
/// <remarks>
/// WHY: a plain inbox is not enough here. Sending an e-mail is a side effect outside our database, so it cannot be
/// committed in one transaction with an inbox entry. An entry before the send gives "at most once" (a crash after the
/// entry loses the e-mail), an entry after the send gives "at least once" (a crash before the entry sends it twice). The
/// history therefore has three states: reservation (<see cref="NotificationStatus.Pending"/>), send, outcome. A
/// reservation found on redelivery says the previous attempt may have reached the provider and we must check with it.
/// </remarks>
public interface INotificationLog
{
    /// <summary>A quick duplicate check (point read) before we perform the more expensive steps.</summary>
    Task<bool> IsCompletedAsync(NotificationDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves the send, or takes over the reservation of an attempt that was interrupted longer ago than the lease time.
    /// </summary>
    /// <returns><c>null</c> when the notification already has an outcome (sent, skipped or rejected).</returns>
    /// <exception cref="SharedKernel.Persistence.ConcurrencyConflictException">
    /// The same send is being processed right now by another instance (the reservation is fresh).
    /// </exception>
    Task<NotificationClaim?> TryClaimAsync(NotificationDraft draft, CancellationToken cancellationToken);

    Task CompleteAsync(NotificationClaim claim, NotificationCompletion completion, CancellationToken cancellationToken);
}

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Shop address the links in messages point to, e.g. <c>https://shopflow.example</c>.</summary>
    [Required]
    public Uri? ShopBaseUrl { get; set; }

    /// <summary>Distribution list of the operations team (warehouse alerts).</summary>
    [Required]
    [EmailAddress]
    public string OperationsEmail { get; set; } = string.Empty;

    /// <summary>Language used when the customer's profile gives none or an unsupported one.</summary>
    public string DefaultLanguage { get; set; } = "pl";

    /// <summary>Tracking link template per carrier, with a <c>{trackingNumber}</c> placeholder.</summary>
    public Dictionary<string, string> CarrierTrackingUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class NotificationsApplicationExtensions
{
    public static IServiceCollection AddNotificationsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NotificationTemplates>();
        services.TryAddScoped<NotificationSender>();

        return services;
    }
}
