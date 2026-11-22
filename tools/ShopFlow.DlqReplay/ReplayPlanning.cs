using System.Globalization;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ShopFlow.Contracts;

namespace ShopFlow.DlqReplay;

/// <summary>Where we read from: a queue's DLQ or a subscription's DLQ.</summary>
internal sealed record DeadLetterSource(string? Queue, string? Topic, string? Subscription)
{
    public override string ToString() => Queue ?? $"{Topic}/subscriptions/{Subscription}";
}

/// <summary>Where we replay to, and whether the message needs a <c>ReplayTarget</c> marker.</summary>
internal sealed record ReplayTarget(string Entity, string? ReplayTargetSubscription);

/// <summary>
/// Determines the replay target from the topology file (the same one Bicep creates the entities from).
/// </summary>
/// <remarks>
/// AZURE: a message cannot be sent to a subscription directly, only to a topic, and the topic fans it out to every
/// subscription with a matching filter. Hence three cases:
/// <list type="bullet">
/// <item>A queue's DLQ: back to the same queue.</item>
/// <item>The DLQ of an auto-forwarding subscription (e.g. orders-events/notifications → notifications-inbox): the message got
/// stuck on the forward, so we send it straight to the target queue.</item>
/// <item>The DLQ of a directly read subscription (e.g. orders-events/saga-start): we send to the topic with a
/// <c>ReplayTarget</c> property. The subscription rules in Bicep let such a message through only to the named subscription
/// (the others have a <c>ReplayTarget IS NULL</c> condition), so other consumers do not get it a second time.</item>
/// </list>
/// </remarks>
internal sealed class ReplayTargetResolver
{
    private readonly Dictionary<string, Dictionary<string, string?>> _forwardsByTopic;
    private readonly HashSet<string> _queues;

    private ReplayTargetResolver(HashSet<string> queues, Dictionary<string, Dictionary<string, string?>> forwardsByTopic)
    {
        _queues = queues;
        _forwardsByTopic = forwardsByTopic;
    }

    public static ReplayTargetResolver FromEmbeddedTopology()
    {
        using var stream = typeof(ReplayTargetResolver).Assembly.GetManifestResourceStream("messaging-topology.json")
            ?? throw new InvalidOperationException("The messaging topology is not embedded in the tool.");
        return FromJson(JsonDocument.Parse(stream));
    }

    public static ReplayTargetResolver FromJson(JsonDocument topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var serviceBus = topology.RootElement.GetProperty("serviceBus");

        var queues = serviceBus.GetProperty("queues").EnumerateArray()
            .Select(queue => queue.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var forwards = serviceBus.GetProperty("topics").EnumerateArray().ToDictionary(
            topic => topic.GetProperty("name").GetString()!,
            topic => topic.GetProperty("subscriptions").EnumerateArray().ToDictionary(
                subscription => subscription.GetProperty("name").GetString()!,
                subscription => subscription.TryGetProperty("forwardTo", out var forward) && forward.ValueKind == JsonValueKind.String ? forward.GetString() : null,
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        return new ReplayTargetResolver(queues, forwards);
    }

    public ReplayTarget Resolve(DeadLetterSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Queue is { } queue)
        {
            return _queues.Contains(queue)
                ? new ReplayTarget(queue, null)
                : throw new ArgumentException($"Queue '{queue}' is not part of the messaging topology.", nameof(source));
        }

        if (source.Topic is not { } topic || source.Subscription is not { } subscription
            || !_forwardsByTopic.TryGetValue(topic, out var subscriptions)
            || !subscriptions.TryGetValue(subscription, out var forwardTo))
        {
            throw new ArgumentException($"Subscription '{source}' is not part of the messaging topology.", nameof(source));
        }

        return forwardTo is not null ? new ReplayTarget(forwardTo, null) : new ReplayTarget(topic, subscription);
    }
}

internal sealed record ReplayFilter(IReadOnlySet<string> MessageIds, string? Reason, bool Force);

internal abstract record ReplayDecision
{
    public sealed record Replay(ServiceBusMessage Message) : ReplayDecision;

    public sealed record Skip(string Reason) : ReplayDecision;
}

/// <summary>
/// Decision for one DLQ message: replay it (and in what form) or leave it.
/// </summary>
internal static class ReplayPlanner
{
    /// <summary>Property the subscription rules use to route a replayed message (<c>replay.property</c> in the topology).</summary>
    public const string ReplayTargetProperty = "ReplayTarget";

    /// <summary>After this many replays a message stops being "unlucky" and becomes poison that someone has to look at.</summary>
    public const int MaxReplays = 3;

    /// <summary>The duplicate detection window of the command queues (duplicateDetectionWindow in the topology).</summary>
    public static readonly TimeSpan DuplicateDetectionWindow = TimeSpan.FromMinutes(10);

    public static ReplayDecision Plan(ServiceBusReceivedMessage message, ReplayTarget target, ReplayFilter filter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.MessageIds.Count > 0 && !filter.MessageIds.Contains(message.MessageId))
        {
            return new ReplayDecision.Skip("not_selected");
        }

        if (filter.Reason is { } reason && !string.Equals(message.DeadLetterReason, reason, StringComparison.Ordinal))
        {
            return new ReplayDecision.Skip("other_reason");
        }

        var replayCount = ReadReplayCount(message);
        if (replayCount >= MaxReplays && !filter.Force)
        {
            // WHY: a message that comes back to the DLQ after three replays does not have a transient problem. Another replay is a loop
            // that burns attempts and clutters the logs; a code or data fix is needed.
            return new ReplayDecision.Skip("replay_limit_reached");
        }

        // AZURE: Service Bus silently drops a message with an identifier seen within the duplicate detection window. A replay right
        // after the message hit the DLQ "succeeds" (the send raises no error), but the message never reaches the consumer.
        if (now - message.EnqueuedTime < DuplicateDetectionWindow && !filter.Force)
        {
            return new ReplayDecision.Skip("inside_duplicate_detection_window");
        }

        var replay = new ServiceBusMessage(message);

        // WHY: the message identifier stays the same. If the first processing partly succeeded (inbox saved, completion lost), the
        // consumer's inbox recognises the repeat. A new identifier would make the consumer treat it as a new message.
        replay.ApplicationProperties.Remove("DeadLetterReason");
        replay.ApplicationProperties.Remove("DeadLetterErrorDescription");
        replay.ApplicationProperties[MessageHeaders.ReplayedFromDeadLetter] = true;
        replay.ApplicationProperties[MessageHeaders.ReplayCount] = replayCount + 1;
        replay.ApplicationProperties["LastDeadLetterReason"] = message.DeadLetterReason ?? "unknown";

        if (target.ReplayTargetSubscription is { } subscription)
        {
            replay.ApplicationProperties[ReplayTargetProperty] = subscription;
        }

        return new ReplayDecision.Replay(replay);
    }

    private static int ReadReplayCount(ServiceBusReceivedMessage message) =>
        message.ApplicationProperties.TryGetValue(MessageHeaders.ReplayCount, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;
}
