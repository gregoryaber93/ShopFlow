using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Communication.Email;
using Azure.Communication.Sms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging.Failures;
using ShopFlow.Notifications.Application;

namespace ShopFlow.Notifications.Infrastructure.Channels;

/// <summary>
/// Azure Communication Services: one resource for e-mail and SMS.
/// </summary>
/// <remarks>
/// AZURE: ACS is connected to an Email Communication Service with a verified sender domain (SPF, DKIM and DMARC records in
/// DNS, deployed by Bicep). Without them messages land in spam or are rejected.
/// AZURE: ACS offers no private endpoint. Traffic goes through the public endpoint via the NAT Gateway, authenticated by
/// the managed identity (Communication and Email Service Owner role on the ACS resource), without an access key.
/// </remarks>
public sealed class CommunicationOptions
{
    public const string SectionName = "Communication";

    /// <summary>E.g. <c>https://acs-shopflow-prod.europe.communication.azure.com</c>. The address only, no key.</summary>
    [Required]
    public Uri? Endpoint { get; set; }

    /// <summary>Sender address in a verified domain, e.g. <c>DoNotReply@notify.shopflow.example</c>.</summary>
    [Required]
    [EmailAddress]
    public string EmailSender { get; set; } = string.Empty;

    /// <summary>Where customer replies go (the support mailbox), since the sender is "no-reply".</summary>
    public string? EmailReplyTo { get; set; }

    /// <summary>SMS sender number bought in ACS, in E.164 format.</summary>
    [Required]
    public string SmsSender { get; set; } = string.Empty;
}

/// <summary>
/// E-mail through ACS with an idempotency key and a check of the previous attempt.
/// </summary>
/// <remarks>
/// AZURE: sending in ACS is a long-running operation. <c>WaitUntil.Started</c> returns when the service has accepted the
/// message into its queue, without waiting for delivery (which takes seconds to minutes). ACS publishes the delivery status
/// later as the <c>EmailDeliveryReportReceived</c> Event Grid event; "accepted" is enough for us here.
/// AZURE: a new ACS Email resource has low default send limits (a few dozen messages per minute), raised through a support
/// request. Exceeding them ends with 429, which the SDK retries and then our classifier hands the message back to Service
/// Bus. That is why host.json limits concurrency and Bicep caps the number of instances.
/// </remarks>
internal sealed partial class AcsEmailSender(EmailClient client, IOptions<CommunicationOptions> options, ILogger<AcsEmailSender> logger) : IEmailSender
{
    public async Task<DeliveryResult> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        // WHY: the documentation does not guarantee that the service rejects a second send with the same operation identifier.
        // Instead of relying on that, on a retry we ask for the status of the operation with our deterministic identifier.
        // If the previous attempt reached ACS, we do not send a second time.
        if (email.IsRetry && await FindPreviousAsync(email.OperationId, cancellationToken).ConfigureAwait(false) is { } previous)
        {
            LogPreviousAttemptFound(email.OperationId);
            return previous;
        }

        var message = Build(email, options.Value);

        try
        {
            var operation = await client.SendAsync(WaitUntil.Started, message, email.OperationId, cancellationToken).ConfigureAwait(false);
            return DeliveryResult.Sent(operation.Id);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // An internal SDK retry may have hit the operation created by the first, "lost" attempt.
            if (await FindPreviousAsync(email.OperationId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return existing;
            }

            throw;
        }
        catch (RequestFailedException exception) when (exception.Status == 400)
        {
            // An invalid address or content. The next attempt would give the same result.
            return DeliveryResult.Rejected(exception.ErrorCode ?? "email_bad_request");
        }
    }

    internal static EmailMessage Build(OutgoingEmail email, CommunicationOptions options)
    {
        var content = new global::Azure.Communication.Email.EmailContent(email.Content.Subject)
        {
            Html = email.Content.Html,
            PlainText = email.Content.PlainText,
        };

        var message = new EmailMessage(options.EmailSender, new EmailRecipients([new EmailAddress(email.To, email.DisplayName)]), content)
        {
            // WHY: transactional messages without open and click tracking. Tracking replaces links with redirects (the SAS link to
            // the invoice would no longer look like a shop link) and requires the customer's consent.
            UserEngagementTrackingDisabled = true,
        };

        if (!string.IsNullOrWhiteSpace(options.EmailReplyTo))
        {
            message.ReplyTo.Add(new EmailAddress(options.EmailReplyTo));
        }

        // Lets support find the history entry from the headers of a message forwarded by the customer.
        message.Headers.Add("X-ShopFlow-Notification-Id", email.OperationId.ToString("D"));
        return message;
    }

    /// <remarks>
    /// AZURE: a long-running operation can be rehydrated from its identifier alone (<c>new EmailSendOperation(id, client)</c>)
    /// and queried for its status. All LROs in the Azure SDK work this way: the identifier is enough to return to an operation
    /// from another process, e.g. after an instance restart.
    /// </remarks>
    private async Task<DeliveryResult?> FindPreviousAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var operation = new EmailSendOperation(operationId.ToString("D"), client);

        try
        {
            await operation.UpdateStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // The previous attempt did not reach ACS.
            return null;
        }
        catch (RequestFailedException exception) when (exception.Status is >= 200 and < 300)
        {
            // The operation exists but ended in failure (e.g. the address is on the suppression list). The Azure SDK reports a
            // failed LRO with an exception even though the status query itself succeeded. Sending again would give the same result.
            return DeliveryResult.Rejected(exception.ErrorCode ?? "email_failed");
        }

        // Accepted, in progress or sent: either way we do not send a second time.
        return DeliveryResult.Sent(operation.Id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email operation {OperationId} already exists in ACS, not sending again")]
    private partial void LogPreviousAttemptFound(Guid operationId);
}

/// <summary>SMS through ACS, with an "at most once" rule.</summary>
internal sealed partial class AcsSmsSender(SmsClient client, IOptions<CommunicationOptions> options, ILogger<AcsSmsSender> logger) : ISmsSender
{
    public async Task<DeliveryResult> SendAsync(OutgoingSms sms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sms);

        // WHY: unlike e-mail. The SMS API does not let us check a send's status by our identifier (the SDK generates the
        // repeatability headers itself, new on every call). When the previous attempt's outcome is unclear we choose
        // "at most once": a lost SMS breaks nothing, because the customer already got the e-mail, while a duplicate costs money
        // and annoys.
        if (sms.IsRetry)
        {
            LogRetrySkipped(sms.OperationId);
            return DeliveryResult.Rejected("previous_attempt_unknown");
        }

        var response = await client
            .SendAsync(options.Value.SmsSender, sms.To, sms.Content.Text, new SmsSendOptions(enableDeliveryReport: true) { Tag = sms.OperationId.ToString("D") }, cancellationToken)
            .ConfigureAwait(false);

        var result = response.Value;
        if (result.Successful)
        {
            return DeliveryResult.Sent(result.MessageId);
        }

        // Outcome per recipient: 429 and 5xx are transient (exception → message retry); the rest is a permanent rejection of the
        // number (e.g. invalid, blocked by the operator).
        if (result.HttpStatusCode is 429 or >= 500)
        {
            throw new RequestFailedException(result.HttpStatusCode, result.ErrorMessage ?? "SMS send failed.");
        }

        return DeliveryResult.Rejected($"sms_{result.HttpStatusCode}");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMS {OperationId} not retried: previous attempt outcome is unknown")]
    private partial void LogRetrySkipped(Guid operationId);
}

/// <summary>Azure SDK (ACS) response codes for the "retry or dead-letter" decision.</summary>
internal sealed class AzureRequestFailureClassifier : IFailureClassifier
{
    public bool TryClassify(Exception exception, out FailureClassification classification)
    {
        if (exception is not RequestFailedException failed)
        {
            classification = default;
            return false;
        }

        // AZURE: the adapters turn permanent rejections (400) into a Rejected result before an exception gets here. What is left
        // is throttling (429), service errors (5xx) and 401/403, i.e. a missing RBAC role after a deployment. We treat all of them
        // as transient: the message waits in the queue and an alert on the growing number of retries calls a human.
        classification = FailureClassification.Transient($"Azure.{failed.Status}", failed.Message);
        return true;
    }
}
