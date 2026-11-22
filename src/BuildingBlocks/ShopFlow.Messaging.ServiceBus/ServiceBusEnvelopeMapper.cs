using System.Diagnostics;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using ShopFlow.Contracts;
using ShopFlow.Messaging.Failures;

namespace ShopFlow.Messaging.ServiceBus;

/// <summary>The only place that knows how an envelope is laid out on a Service Bus message.</summary>
public static class ServiceBusEnvelopeMapper
{
    /// <summary>
    /// Property in which the Azure SDK carries the W3C trace context (the value has the traceparent format).
    /// </summary>
    public const string DiagnosticIdProperty = "Diagnostic-Id";

    public const string TraceStateProperty = "tracestate";

    public static ServiceBusMessage ToServiceBusMessage(MessageEnvelope envelope, Activity? createActivity = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var message = new ServiceBusMessage(envelope.Payload)
        {
            // AZURE: MessageId is the duplicate detection key. The broker remembers identifiers for the
            // configured window (10 minutes here) and silently drops a repeat.
            MessageId = envelope.MessageId.ToString("D"),

            // AZURE: Subject is one of the fields a subscription's correlation filter can see, and that filter
            // is a hash match, noticeably cheaper than a SQL filter. That is why the message type goes here.
            Subject = envelope.MessageType,

            CorrelationId = envelope.CorrelationId,
            ContentType = ContractJson.ContentType,
        };

        if (envelope.SessionId is not null)
        {
            // AZURE: on a session-enabled entity a message without a SessionId is rejected on send.
            // On an entity without sessions the field is ignored, so we always set it and the same
            // message can be auto-forwarded to a session-enabled queue.
            message.SessionId = envelope.SessionId;
        }

        var properties = message.ApplicationProperties;
        properties[MessageHeaders.ContractVersion] = envelope.ContractVersion.ToString(CultureInfo.InvariantCulture);
        properties[MessageHeaders.Source] = envelope.Source;
        properties[MessageHeaders.OccurredAt] = envelope.OccurredAt;

        if (envelope.CausationId is not null)
        {
            properties[MessageHeaders.CausationId] = envelope.CausationId;
        }

        // WHY: we write the trace context ourselves. The SDK would do it automatically, but it would use the
        // relay's current activity, while we want the consumer to be in the same trace as the operation
        // that produced the message. The SDK skips its own instrumentation when the field is already set.
        var traceParent = createActivity?.Id ?? envelope.TraceParent;
        if (traceParent is not null)
        {
            properties[DiagnosticIdProperty] = traceParent;

            var traceState = createActivity?.TraceStateString ?? envelope.TraceState;
            if (!string.IsNullOrEmpty(traceState))
            {
                properties[TraceStateProperty] = traceState;
            }
        }

        return message;
    }

    /// <exception cref="PermanentMessageException">The message lacks metadata required by the envelope.</exception>
    public static MessageEnvelope ToEnvelope(ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!Guid.TryParse(message.MessageId, out var messageId))
        {
            throw Malformed($"MessageId '{message.MessageId}' is not a GUID.");
        }

        if (string.IsNullOrWhiteSpace(message.Subject))
        {
            throw Malformed("Subject (message type) is missing.");
        }

        var properties = message.ApplicationProperties;

        return new MessageEnvelope
        {
            MessageId = messageId,
            MessageType = message.Subject,
            ContractVersion = ReadContractVersion(properties),
            CorrelationId = string.IsNullOrEmpty(message.CorrelationId) ? message.MessageId : message.CorrelationId,
            CausationId = properties.TryGetValue(MessageHeaders.CausationId, out var causation) ? causation as string : null,
            Source = properties.TryGetValue(MessageHeaders.Source, out var source) && source is string sourceName
                ? sourceName
                : "unknown",
            OccurredAt = ReadOccurredAt(properties, message.EnqueuedTime),
            SessionId = message.SessionId,
            TraceParent = properties.TryGetValue(DiagnosticIdProperty, out var traceParent) ? traceParent as string : null,
            TraceState = properties.TryGetValue(TraceStateProperty, out var traceState) ? traceState as string : null,
            Payload = message.Body.ToMemory(),
        };
    }

    public static DeliveryInfo ToDeliveryInfo(ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new DeliveryInfo(
            message.DeliveryCount,
            message.EnqueuedTime,
            message.ApplicationProperties.ContainsKey(MessageHeaders.ReplayedFromDeadLetter));
    }

    private static int ReadContractVersion(IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(MessageHeaders.ContractVersion, out var raw))
        {
            throw Malformed($"Application property '{MessageHeaders.ContractVersion}' is missing.");
        }

        return raw switch
        {
            string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            int number => number,
            long number => checked((int)number),
            _ => throw Malformed($"Application property '{MessageHeaders.ContractVersion}' has an unsupported value '{raw}'."),
        };
    }

    private static DateTimeOffset ReadOccurredAt(IReadOnlyDictionary<string, object> properties, DateTimeOffset fallback) =>
        properties.TryGetValue(MessageHeaders.OccurredAt, out var raw)
            ? raw switch
            {
                DateTimeOffset value => value,
                DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
                _ => fallback,
            }
            : fallback;

    private static PermanentMessageException Malformed(string description) =>
        new(DeadLetterReasons.MalformedEnvelope, description);
}
