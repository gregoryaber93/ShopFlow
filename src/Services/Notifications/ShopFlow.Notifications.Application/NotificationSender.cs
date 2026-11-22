using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShopFlow.Notifications.Application;

public enum NotificationOutcome
{
    Sent,
    Duplicate,
    Skipped,
    Rejected,
}

/// <summary>
/// One send: reservation in the history, recipient, content, provider, outcome in the history.
/// </summary>
/// <remarks>
/// WHY: the order of the steps is deliberate.
/// <list type="number">
/// <item>First a cheap point read from the history: a duplicate ends at 1 RU, without a Graph call.</item>
/// <item>Then the steps without side effects: the recipient (from Graph) and the content (depends on their language). A
/// transient failure there leaves no trace, so a retry starts from a clean slate.</item>
/// <item>The reservation right before calling the provider. A reservation found later therefore really means "the
/// previous attempt may have reached the provider", not "the previous attempt got no answer from Graph".</item>
/// <item>The outcome after the provider answers. A crash between sending and recording leaves the reservation, and the
/// next attempt (with <c>IsRetry</c>) first asks the provider whether the message already reached it.</item>
/// </list>
/// </remarks>
public sealed partial class NotificationSender(
    INotificationLog log,
    ICustomerDirectory directory,
    IEmailSender email,
    ISmsSender sms,
    IOptions<NotificationOptions> options,
    ILogger<NotificationSender> logger)
{
    public async Task<NotificationOutcome> SendToCustomerAsync(
        NotificationDraft draft,
        Guid customerId,
        Func<Recipient, NotificationContent> render,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(render);

        if (await log.IsCompletedAsync(draft, cancellationToken).ConfigureAwait(false))
        {
            LogDuplicate(draft.Template, draft.NotificationId);
            return NotificationOutcome.Duplicate;
        }

        var contact = await directory.FindAsync(customerId, cancellationToken).ConfigureAwait(false);
        if (contact is null)
        {
            return await SkipAsync(draft, "customer_not_found", cancellationToken).ConfigureAwait(false);
        }

        var address = draft.Channel == NotificationChannel.Email ? contact.Email : contact.MobilePhone;
        if (string.IsNullOrWhiteSpace(address))
        {
            return await SkipAsync(draft, "no_address", cancellationToken).ConfigureAwait(false);
        }

        var recipient = new Recipient(address, contact.DisplayName, ResolveLanguage(contact.PreferredLanguage));
        return await DeliverAsync(draft, recipient, render(recipient), cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationOutcome> SendToOperationsAsync(
        NotificationDraft draft,
        Func<Recipient, NotificationContent> render,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(render);

        if (await log.IsCompletedAsync(draft, cancellationToken).ConfigureAwait(false))
        {
            LogDuplicate(draft.Template, draft.NotificationId);
            return NotificationOutcome.Duplicate;
        }

        var recipient = new Recipient(options.Value.OperationsEmail, "ShopFlow Operations", "pl");
        return await DeliverAsync(draft, recipient, render(recipient), cancellationToken).ConfigureAwait(false);
    }

    private async Task<NotificationOutcome> DeliverAsync(
        NotificationDraft draft,
        Recipient recipient,
        NotificationContent content,
        CancellationToken cancellationToken)
    {
        var claim = await log.TryClaimAsync(draft, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            LogDuplicate(draft.Template, draft.NotificationId);
            return NotificationOutcome.Duplicate;
        }

        var result = (draft.Channel, content) switch
        {
            (NotificationChannel.Email, EmailContent emailContent) => await email
                .SendAsync(new OutgoingEmail(draft.NotificationId, recipient.Address, recipient.DisplayName, emailContent, claim.IsRetry), cancellationToken)
                .ConfigureAwait(false),
            (NotificationChannel.Sms, SmsContent smsContent) => await sms
                .SendAsync(new OutgoingSms(draft.NotificationId, recipient.Address, smsContent, claim.IsRetry), cancellationToken)
                .ConfigureAwait(false),
            _ => throw new UnreachableException($"Template '{draft.Template}' produced {content.GetType().Name} for channel {draft.Channel}."),
        };

        var masked = RecipientMask.Mask(recipient.Address);
        if (result.Accepted)
        {
            await log.CompleteAsync(claim, new NotificationCompletion(NotificationStatus.Sent, masked, result.ProviderMessageId, null), cancellationToken)
                .ConfigureAwait(false);
            LogSent(draft.Template, draft.Channel, draft.NotificationId, claim.Attempt);
            return NotificationOutcome.Sent;
        }

        // WHY: a permanent rejection (e.g. the address does not exist) is not a message processing error. An exception would
        // send the message to the DLQ, and replaying it would give the same result. We record the reason in the history
        // (customer support can see it) and complete the message.
        await log.CompleteAsync(claim, new NotificationCompletion(NotificationStatus.Rejected, masked, null, result.FailureCode), cancellationToken)
            .ConfigureAwait(false);
        LogRejected(draft.Template, draft.Channel, draft.NotificationId, result.FailureCode);
        return NotificationOutcome.Rejected;
    }

    private async Task<NotificationOutcome> SkipAsync(NotificationDraft draft, string reason, CancellationToken cancellationToken)
    {
        // A skip goes into the history too: customer support sees "not sent because the account does not exist"
        // instead of guessing whether the message got lost.
        var claim = await log.TryClaimAsync(draft, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return NotificationOutcome.Duplicate;
        }

        await log.CompleteAsync(claim, new NotificationCompletion(NotificationStatus.Skipped, null, null, reason), cancellationToken)
            .ConfigureAwait(false);
        LogSkipped(draft.Template, draft.NotificationId, reason);
        return NotificationOutcome.Skipped;
    }

    private string ResolveLanguage(string? preferredLanguage)
    {
        // The profile gives a BCP 47 tag (e.g. "pl-PL", "en-US"); we support two languages.
        var language = preferredLanguage?.Split('-', 2)[0].ToUpperInvariant();
        return language switch
        {
            "PL" => "pl",
            "EN" => "en",
            _ => options.Value.DefaultLanguage,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Template} {NotificationId} already handled, skipping duplicate")]
    private partial void LogDuplicate(string template, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Template} sent via {Channel} ({NotificationId}, attempt {Attempt})")]
    private partial void LogSent(string template, NotificationChannel channel, Guid notificationId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Template} via {Channel} rejected by provider ({NotificationId}): {FailureCode}")]
    private partial void LogRejected(string template, NotificationChannel channel, Guid notificationId, string? failureCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Template} {NotificationId} skipped: {Reason}")]
    private partial void LogSkipped(string template, Guid notificationId, string reason);
}

/// <summary>Address for the history and logs: just enough for support to recognise the recipient, nothing more.</summary>
/// <remarks>
/// WHY: the send history lives for 180 days, and logs go to Log Analytics with their own retention. A full address in both
/// places means more copies of personal data to delete on the customer's request. Logs deliberately contain no address at all.
/// </remarks>
public static class RecipientMask
{
    public static string Mask(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            return $"{address[0]}***{address[at..]}";
        }

        return address.Length > 6 ? $"{address[..3]}*****{address[^3..]}" : "***";
    }
}
