using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShopFlow.Messaging.Diagnostics;

/// <summary>
/// Source of messaging traces and metrics. Attribute names are used by the queries in <c>docs/kql/</c>
/// and by alerts, so renaming them is a breaking change for monitoring.
/// </summary>
/// <remarks>
/// AZURE: in Application Insights span attributes land in the <c>customDimensions</c> column of the
/// <c>dependencies</c> (Producer/Client span) and <c>requests</c> (Server/Consumer span) tables. Metrics
/// from the <see cref="Meter"/> go to the <c>customMetrics</c> table.
/// </remarks>
public static class MessagingTelemetry
{
    public const string SourceName = "ShopFlow.Messaging";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> PublishedCounter =
        Meter.CreateCounter<long>("shopflow.messaging.published", "{message}", "Messages accepted by the broker.");

    private static readonly Counter<long> ProcessedCounter =
        Meter.CreateCounter<long>("shopflow.messaging.processed", "{message}", "Messages processed by a consumer, by outcome.");

    private static readonly Histogram<double> ProcessDuration =
        Meter.CreateHistogram<double>("shopflow.messaging.process.duration", "ms", "Time spent in the message handler.");

    private static readonly Counter<long> DeadLetteredCounter =
        Meter.CreateCounter<long>("shopflow.messaging.dead_lettered", "{message}", "Messages moved to the dead-letter queue by the application.");

    private static readonly Histogram<double> OutboxLag =
        Meter.CreateHistogram<double>("shopflow.outbox.lag", "s", "Delay between writing an outbox record and publishing it.");

    public static class Tags
    {
        public const string MessageType = "shopflow.message.type";
        public const string ContractVersion = "shopflow.message.contract_version";
        public const string CorrelationId = "shopflow.correlation.id";
        public const string OrderId = "shopflow.order.id";
        public const string Consumer = "shopflow.consumer";
        public const string Outcome = "shopflow.outcome";
        public const string DeadLetterReason = "shopflow.dead_letter.reason";

        // Names follow the OpenTelemetry semantic conventions for messaging.
        public const string MessagingSystem = "messaging.system";
        public const string MessageId = "messaging.message.id";
        public const string Destination = "messaging.destination.name";
        public const string DeliveryCount = "messaging.servicebus.message.delivery_count";
    }

    /// <summary>
    /// Span for producing a message. Its identifier is stored in the message and becomes the parent
    /// of the processing span on the consumer side.
    /// </summary>
    public static Activity? StartCreate(MessageEnvelope envelope, string destination)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // The relay runs in a different process and at a different time than the operation that wrote the outbox.
        // The parent is therefore the context stored in the envelope, not the relay's current activity.
        var activity = TryParseContext(envelope, out var parent)
            ? ActivitySource.StartActivity($"create {destination}", ActivityKind.Producer, parent)
            : ActivitySource.StartActivity($"create {destination}", ActivityKind.Producer);

        if (activity is null)
        {
            return null;
        }

        SetCommonTags(activity, envelope);
        activity.SetTag(Tags.Destination, destination);
        return activity;
    }

    public static Activity? StartProcess(MessageEnvelope envelope, string consumer, int deliveryCount)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // Under Azure Functions the host has already created an invocation activity linked to the message,
        // so we become its child. Outside Functions we restore the parent from the envelope.
        var activity = Activity.Current is null && TryParseContext(envelope, out var parent)
            ? ActivitySource.StartActivity($"process {envelope.MessageType}", ActivityKind.Consumer, parent)
            : ActivitySource.StartActivity($"process {envelope.MessageType}", ActivityKind.Consumer);

        if (activity is null)
        {
            return null;
        }

        SetCommonTags(activity, envelope);
        activity.SetTag(Tags.Consumer, consumer);
        activity.SetTag(Tags.DeliveryCount, deliveryCount);
        return activity;
    }

    public static void RecordPublished(string messageType, string destination, int count = 1) =>
        PublishedCounter.Add(
            count,
            new KeyValuePair<string, object?>(Tags.MessageType, messageType),
            new KeyValuePair<string, object?>(Tags.Destination, destination));

    public static void RecordProcessed(string messageType, string consumer, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { Tags.MessageType, messageType },
            { Tags.Consumer, consumer },
            { Tags.Outcome, outcome },
        };

        ProcessedCounter.Add(1, tags);
        ProcessDuration.Record(duration.TotalMilliseconds, tags);
    }

    public static void RecordDeadLettered(string messageType, string consumer, string reason) =>
        DeadLetteredCounter.Add(
            1,
            new KeyValuePair<string, object?>(Tags.MessageType, messageType),
            new KeyValuePair<string, object?>(Tags.Consumer, consumer),
            new KeyValuePair<string, object?>(Tags.DeadLetterReason, reason));

    /// <summary>Outbox lag: the main health indicator of a relay and the basis of the "outbox lag" alert.</summary>
    public static void RecordOutboxLag(TimeSpan lag, string source) =>
        OutboxLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("shopflow.outbox.source", source));

    private static bool TryParseContext(MessageEnvelope envelope, out ActivityContext context)
    {
        context = default;
        return envelope.TraceParent is not null
            && ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out context);
    }

    private static void SetCommonTags(Activity activity, MessageEnvelope envelope)
    {
        activity.SetTag(Tags.MessagingSystem, "servicebus");
        activity.SetTag(Tags.MessageId, envelope.MessageId.ToString("D"));
        activity.SetTag(Tags.MessageType, envelope.MessageType);
        activity.SetTag(Tags.ContractVersion, envelope.ContractVersion);
        activity.SetTag(Tags.CorrelationId, envelope.CorrelationId);

        if (envelope.SessionId is not null)
        {
            activity.SetTag(Tags.OrderId, envelope.SessionId);
        }
    }
}
