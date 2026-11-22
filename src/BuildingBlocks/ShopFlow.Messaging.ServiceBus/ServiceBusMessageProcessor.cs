using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging.Diagnostics;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;

namespace ShopFlow.Messaging.ServiceBus;

/// <summary>
/// Settles a message in the broker. Two implementations: for <c>ServiceBusProcessor</c>
/// (containers) and for the Azure Functions trigger.
/// </summary>
public interface IMessageSettlement
{
    Task CompleteAsync(CancellationToken cancellationToken);

    Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken);
}

/// <summary>
/// Policy shared by all hosts: what to do with a message after success and after each kind of error.
/// </summary>
/// <remarks>
/// AZURE: a message received in PeekLock mode is locked for other receivers for the "lock duration".
/// The receiver must settle it with one of these operations:
/// <list type="bullet">
/// <item><b>Complete</b> removes the message.</item>
/// <item><b>Abandon</b> releases the lock; the message is available again immediately and DeliveryCount grows.</item>
/// <item><b>DeadLetter</b> moves it to the <c>$DeadLetterQueue</c> sub-queue with the given reason.</item>
/// </list>
/// When DeliveryCount exceeds the entity's maxDeliveryCount, the broker moves the message to the DLQ itself.
/// </remarks>
public sealed partial class ServiceBusMessageProcessor(
    MessageDispatcher dispatcher,
    MessageFailureClassifier failureClassifier,
    IOptions<ServiceBusProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<ServiceBusMessageProcessor> logger)
{
    // Service Bus truncates the error description in the DLQ; a short text also leaves room in the header size limit.
    private const int MaxDeadLetterDescriptionLength = 1024;

    private readonly ServiceBusProcessingOptions _options = options.Value;

    /// <summary>
    /// Processes a message and settles it in the broker. An exception from this method means a transient
    /// error: the host (Functions or ServiceBusProcessor) then abandons the message.
    /// </summary>
    public Task ProcessAsync(
        ServiceBusReceivedMessage message,
        IMessageSettlement settlement,
        ConsumerEndpoint endpoint,
        CancellationToken cancellationToken) =>
        ProcessAsync(message, settlement, endpoint, configureScope: null, cancellationToken);

    /// <inheritdoc cref="ProcessAsync(ServiceBusReceivedMessage, IMessageSettlement, ConsumerEndpoint, CancellationToken)"/>
    /// <param name="message">Message from the broker.</param>
    /// <param name="settlement">Settlement in the broker.</param>
    /// <param name="endpoint">The consumer.</param>
    /// <param name="configureScope">See <see cref="MessageDispatcher.DispatchAsync(MessageEnvelope, ConsumerEndpoint, DeliveryInfo, Action{IServiceProvider}?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ProcessAsync(
        ServiceBusReceivedMessage message,
        IMessageSettlement settlement,
        ConsumerEndpoint endpoint,
        Action<IServiceProvider>? configureScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(endpoint);

        MessageEnvelope envelope;
        try
        {
            envelope = ServiceBusEnvelopeMapper.ToEnvelope(message);
        }
        catch (PermanentMessageException exception)
        {
            await DeadLetterAsync(message, settlement, endpoint, exception.Reason, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await dispatcher
                .DispatchAsync(envelope, endpoint, ServiceBusEnvelopeMapper.ToDeliveryInfo(message), configureScope, cancellationToken)
                .ConfigureAwait(false);

            // Handled, Ignored and Duplicate end the same way: the message is no longer needed.
            await settlement.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The instance is shutting down (scale-in, deployment). We do not settle: the lock will expire or
            // the host will abandon the message, and another instance will receive it.
            throw;
        }
        catch (Exception exception)
        {
            var failure = failureClassifier.Classify(exception);

            if (failure.Kind == FailureKind.Permanent)
            {
                await DeadLetterAsync(message, settlement, endpoint, failure.Reason, failure.Description, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            LogTransientFailure(exception, message.MessageId, message.Subject, endpoint.Name, message.DeliveryCount, failure.Reason);

            // AZURE: Service Bus has no delayed redelivery. After Abandon the message is available immediately,
            // so during a dependency outage all attempts would burn out within seconds and the whole queue
            // would end up in the DLQ.
            // WHY: before giving the message back we hold the lock for a growing amount of time. This is the
            // simplest backoff that preserves ordering within a session (unlike rescheduling a copy of the
            // message) and also works for subscriptions read directly. Sleeping invocations occupy concurrency
            // slots, so during an outage the consumer slows down fetching on its own.
            // The cost is function execution time. Longer outages are handled by the DLQ and the replay tool.
            await Task.Delay(GetRedeliveryDelay(message.DeliveryCount), timeProvider, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    internal TimeSpan GetRedeliveryDelay(int deliveryCount)
    {
        var exponent = Math.Clamp(deliveryCount - 1, 0, 10);
        var delay = _options.RedeliveryBaseDelay * Math.Pow(2, exponent);
        return delay < _options.RedeliveryMaxDelay ? delay : _options.RedeliveryMaxDelay;
    }

    private async Task DeadLetterAsync(
        ServiceBusReceivedMessage message,
        IMessageSettlement settlement,
        ConsumerEndpoint endpoint,
        string reason,
        string description,
        CancellationToken cancellationToken)
    {
        var truncated = description.Length <= MaxDeadLetterDescriptionLength
            ? description
            : description[..MaxDeadLetterDescriptionLength];

        await settlement.DeadLetterAsync(reason, truncated, cancellationToken).ConfigureAwait(false);

        MessagingTelemetry.RecordDeadLettered(message.Subject ?? "unknown", endpoint.Name, reason);
        LogDeadLettered(message.MessageId, message.Subject, endpoint.Name, reason, truncated);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient failure ({Reason}) processing message {MessageId} ({MessageType}) in {Consumer}, delivery {DeliveryCount}; the message will be redelivered")]
    private partial void LogTransientFailure(Exception exception, string messageId, string? messageType, string consumer, int deliveryCount, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} ({MessageType}) dead-lettered by {Consumer}: {Reason} - {Description}")]
    private partial void LogDeadLettered(string messageId, string? messageType, string consumer, string reason, string description);
}
