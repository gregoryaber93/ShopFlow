using System.Text.Json;
using Azure.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Payments.Application.Invoicing;

namespace ShopFlow.Payments.Functions.Functions;

/// <summary>
/// Blob Storage events (BlobCreated in the invoices container) delivered by Event Grid to a Service Bus queue.
/// </summary>
/// <remarks>
/// AZURE: the chain is Blob Storage → Event Grid system topic → subscription with a filter (type BlobCreated, path
/// /containers/invoices/, .pdf suffix) → invoice-blob-events queue → this function.
/// <list type="bullet">
/// <item>Event Grid does not deliver events to endpoints that are reachable only privately, and the functions have no
/// public entry point. A Service Bus queue is therefore the intermediary that Event Grid can reach as a trusted service
/// (the system topic's managed identity, Data Sender role on this queue).</item>
/// <item>The message body is an event in CloudEvents 1.0 format, not our envelope (no Subject or ContractVersion).
/// That is why this function does not use the shared adapter, which would reject the message as malformed; it reads
/// the CloudEvent itself and settles the message using the same error classification.</item>
/// <item>Event Grid delivers at least once, with its own retries (up to 24 h) and dead-lettering to a blob container
/// when the queue is unavailable. Duplicates are removed by the inbox using the event identifier.</item>
/// </list>
/// </remarks>
public sealed partial class InvoiceBlobEventsFunction(
    IServiceScopeFactory scopeFactory,
    MessageFailureClassifier failureClassifier,
    TimeProvider timeProvider,
    ILogger<InvoiceBlobEventsFunction> logger)
{
    private const string BlobCreated = "Microsoft.Storage.BlobCreated";

    [Function("invoice-blob-events")]
    public async Task Run(
        [ServiceBusTrigger(MessagingTopology.Queues.InvoiceBlobEvents, Connection = "ServiceBus", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(actions);

        CloudEvent? cloudEvent;
        Uri? blobUrl;
        try
        {
            cloudEvent = CloudEvent.Parse(message.Body);
            blobUrl = cloudEvent?.Data is { } data && JsonDocument.Parse(data.ToMemory()).RootElement.TryGetProperty("url", out var url)
                ? new Uri(url.GetString()!)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedEnvelope, deadLetterErrorDescription: exception.Message, cancellationToken: cancellationToken);
            return;
        }

        if (cloudEvent is null || cloudEvent.Type != BlobCreated || blobUrl is null)
        {
            // A different event type (e.g. after a subscription filter change): not ours, acknowledge it.
            await actions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(IncomingContext(cloudEvent, message));

            try
            {
                await scope.ServiceProvider.GetRequiredService<InvoiceRegistrationService>().RegisterAsync(blobUrl, cancellationToken);
                await actions.CompleteMessageAsync(message, cancellationToken);
            }
            catch (DuplicateMessageException)
            {
                await actions.CompleteMessageAsync(message, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failure = failureClassifier.Classify(exception);
                if (failure.Kind == FailureKind.Permanent)
                {
                    LogDeadLettered(cloudEvent.Id, failure.Reason, failure.Description);
                    await actions.DeadLetterMessageAsync(message, deadLetterReason: failure.Reason, deadLetterErrorDescription: failure.Description, cancellationToken: cancellationToken);
                    return;
                }

                await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
                throw;
            }
        }
    }

    private MessageContext IncomingContext(CloudEvent cloudEvent, ServiceBusReceivedMessage message) => new()
    {
        // The Event Grid event identifier as the "incoming message": the inbox rejects its duplicate.
        MessageId = DeterministicGuid.Create($"eventgrid/{cloudEvent.Id}"),
        MessageType = cloudEvent.Type,
        ContractVersion = 1,
        CorrelationId = cloudEvent.Id,
        Source = "eventgrid",
        OccurredAt = cloudEvent.Time ?? timeProvider.GetUtcNow(),
        Consumer = "invoice-registration",
        DeliveryCount = message.DeliveryCount,
        EnqueuedAt = message.EnqueuedTime,
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Blob event {EventId} dead-lettered: {Reason} - {Description}")]
    private partial void LogDeadLettered(string eventId, string reason, string description);
}
