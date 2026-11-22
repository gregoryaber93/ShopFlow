using System.Globalization;
using Azure.Messaging.ServiceBus;

namespace ShopFlow.DlqReplay;

internal sealed class DeadLetterTool(ServiceBusClient client, ReplayTargetResolver targets, TimeProvider timeProvider, TextWriter output)
{
    /// <summary>Peek without locking: messages stay in the DLQ and nobody can tell someone read them.</summary>
    public async Task<int> PeekAsync(DlqArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        await using var receiver = CreateDeadLetterReceiver(arguments.Source, ServiceBusReceiveMode.PeekLock);
        var messages = await receiver.PeekMessagesAsync(arguments.Max, cancellationToken: cancellationToken);

        await output.WriteLineAsync($"DLQ {arguments.Source}: {messages.Count} message(s) shown (max {arguments.Max})");
        foreach (var message in messages)
        {
            await output.WriteLineAsync(Describe(message));
        }

        return 0;
    }

    /// <summary>
    /// Receives DLQ messages in peek-lock mode, sends a copy to the target and only then removes the original.
    /// </summary>
    /// <remarks>
    /// WHY: the "send, then complete" order. If the process crashes in between, the message stays in the DLQ and is replayed a
    /// second time on the next run; the consumer recognises the duplicate by MessageId in its inbox. The reverse order could
    /// lose the message. A Service Bus transaction does not cover both operations here, because the DLQ and the target are
    /// different entities (for a subscription even different topics/queues).
    /// </remarks>
    public async Task<int> ReplayAsync(DlqArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var target = targets.Resolve(arguments.Source);
        await output.WriteLineAsync($"Replaying DLQ {arguments.Source} → {target.Entity}{(target.ReplayTargetSubscription is { } s ? $" (ReplayTarget={s})" : string.Empty)}{(arguments.DryRun ? " [dry run]" : string.Empty)}");

        await using var receiver = CreateDeadLetterReceiver(arguments.Source, ServiceBusReceiveMode.PeekLock);
        await using var sender = client.CreateSender(target.Entity);

        int replayed = 0, skipped = 0;
        var messages = await receiver.ReceiveMessagesAsync(arguments.Max, TimeSpan.FromSeconds(5), cancellationToken);

        foreach (var message in messages)
        {
            var decision = ReplayPlanner.Plan(message, target, arguments.Filter, timeProvider.GetUtcNow());

            switch (decision)
            {
                case ReplayDecision.Replay replay when !arguments.DryRun:
                    await sender.SendMessageAsync(replay.Message, cancellationToken);
                    await receiver.CompleteMessageAsync(message, cancellationToken);
                    replayed++;
                    await output.WriteLineAsync($"  replayed  {message.MessageId}");
                    break;

                case ReplayDecision.Replay:
                    await receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                    replayed++;
                    await output.WriteLineAsync($"  would replay {message.MessageId}");
                    break;

                case ReplayDecision.Skip skip:
                    // Abandoning returns the message to the DLQ immediately (instead of waiting for the lock to expire).
                    await receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                    skipped++;
                    await output.WriteLineAsync($"  skipped   {message.MessageId} ({skip.Reason})");
                    break;
            }
        }

        await output.WriteLineAsync($"Done: {replayed} {(arguments.DryRun ? "would be replayed" : "replayed")}, {skipped} skipped.");
        return 0;
    }

    private ServiceBusReceiver CreateDeadLetterReceiver(DeadLetterSource source, ServiceBusReceiveMode mode)
    {
        // AZURE: the DLQ is a sub-queue of the entity (<entity>/$deadletterqueue). The DLQ of a session-enabled queue is also read without sessions.
        var options = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = mode };
        return source.Queue is { } queue
            ? client.CreateReceiver(queue, options)
            : client.CreateReceiver(source.Topic!, source.Subscription!, options);
    }

    private static string Describe(ServiceBusReceivedMessage message) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"  {message.MessageId}  {message.Subject,-28} enqueued {message.EnqueuedTime:u}  deliveries {message.DeliveryCount}  reason {message.DeadLetterReason}: {Truncate(message.DeadLetterErrorDescription, 120)}");

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength] + "…";
}
