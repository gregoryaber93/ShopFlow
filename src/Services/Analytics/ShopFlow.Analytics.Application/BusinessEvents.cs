using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Contracts.Analytics.V1;
using ShopFlow.Messaging;

namespace ShopFlow.Analytics.Application;

public static class AnalyticsApplicationExtensions
{
    public static IServiceCollection AddAnalyticsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ClickstreamIngestor>();

        return services;
    }
}

public interface IBusinessEventPublisher
{
    /// <summary>Returns only after Event Hubs has accepted all events.</summary>
    Task PublishAsync(IReadOnlyList<BusinessEvent> events, CancellationToken cancellationToken);
}

/// <summary>Rewrites a Service Bus envelope into a <c>business-events</c> hub event.</summary>
/// <remarks>
/// WHY: the forwarder does not deserialize contracts into C# types. The payload passes through unchanged as JSON, and we
/// extract only two fields common to almost every event (<c>orderId</c>, <c>customerId</c>). A new event or a new contract
/// version therefore flows to analytics immediately, without updating the contracts package or redeploying the forwarder.
/// </remarks>
public static class BusinessEventMapper
{
    public static BusinessEvent FromEnvelope(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var document = JsonDocument.Parse(envelope.Payload);
        var payload = document.RootElement.Clone();

        return new BusinessEvent
        {
            EventId = envelope.MessageId,
            EventType = envelope.MessageType,
            ContractVersion = envelope.ContractVersion,
            Source = envelope.Source,
            OrderId = Guid.TryParse(envelope.SessionId, out var sessionOrderId) ? sessionOrderId : ReadGuid(payload, "orderId"),
            CustomerId = ReadGuid(payload, "customerId"),
            CorrelationId = envelope.CorrelationId,
            OccurredAt = envelope.OccurredAt,
            Payload = payload,
        };
    }

    /// <summary>
    /// Event Hubs partition key: the order, and for events without an order (e.g. StockLevelLow) the event itself.
    /// </summary>
    /// <remarks>
    /// AZURE: the same key always lands in the same partition, so one consumer reads an order's events in write order.
    /// Events without an order need no ordering, and a random key spreads them evenly across partitions.
    /// </remarks>
    public static string PartitionKey(BusinessEvent businessEvent)
    {
        ArgumentNullException.ThrowIfNull(businessEvent);
        return (businessEvent.OrderId ?? businessEvent.EventId).ToString("D");
    }

    private static Guid? ReadGuid(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out var guid)
            ? guid
            : null;
}
