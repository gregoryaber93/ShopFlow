using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using ShopFlow.Messaging.Diagnostics;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Outbox;

namespace ShopFlow.Messaging.ServiceBus;

/// <summary>Publishes envelopes to Service Bus queues and topics.</summary>
public sealed partial class ServiceBusMessagePublisher(
    ServiceBusClient client,
    ILogger<ServiceBusMessagePublisher> logger) : IMessagePublisher, IAsyncDisposable
{
    // AZURE: ServiceBusClient holds one AMQP connection and each sender is a link on that connection.
    // Both are thread-safe and meant to live for the whole lifetime of the process. Creating a sender
    // per message means a new AMQP link (several round trips) every time and is the most common cause
    // of a "slow Service Bus".
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.OrdinalIgnoreCase);

    public async Task PublishAsync(IReadOnlyCollection<OutboundMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var group in messages.GroupBy(message => message.Destination, StringComparer.OrdinalIgnoreCase))
        {
            var sender = _senders.GetOrAdd(group.Key, client.CreateSender);
            await SendInBatchesAsync(sender, [.. group], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendInBatchesAsync(
        ServiceBusSender sender,
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken)
    {
        // AZURE: ServiceBusMessageBatch enforces the size limit on the client side (the limit comes from
        // the broker and depends on the tier). Sending a list of messages "blindly" fails with a
        // MessageSizeExceeded exception for the whole list when the total exceeds the limit.
        // A batch is also atomic: the broker accepts all messages or none.
        var batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var outbound in messages)
            {
                using var createActivity = MessagingTelemetry.StartCreate(outbound.Envelope, sender.EntityPath);
                var message = ServiceBusEnvelopeMapper.ToServiceBusMessage(outbound.Envelope, createActivity);

                if (batch.TryAddMessage(message))
                {
                    continue;
                }

                if (batch.Count == 0)
                {
                    throw TooLarge(outbound, batch.MaxSizeInBytes);
                }

                // The current batch is full: send it and start the next one. Ordering is preserved.
                await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);

                if (!batch.TryAddMessage(message))
                {
                    throw TooLarge(outbound, batch.MaxSizeInBytes);
                }
            }

            if (batch.Count > 0)
            {
                await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            batch.Dispose();
        }

        foreach (var typeGroup in messages.GroupBy(message => message.Envelope.MessageType))
        {
            MessagingTelemetry.RecordPublished(typeGroup.Key, sender.EntityPath, typeGroup.Count());
        }

        LogPublished(messages.Count, sender.EntityPath);
    }

    private static PermanentMessageException TooLarge(OutboundMessage outbound, long maxSizeInBytes) =>
        new(
            DeadLetterReasons.MessageTooLarge,
            $"Message {outbound.Envelope.MessageId} ({outbound.Envelope.ContractId}) exceeds the " +
            $"{maxSizeInBytes}-byte limit of '{outbound.Destination}'. Store large payloads in Blob Storage " +
            "and send a reference instead.");

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _senders.Clear();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {Count} message(s) to {Destination}")]
    private partial void LogPublished(int count, string destination);
}
