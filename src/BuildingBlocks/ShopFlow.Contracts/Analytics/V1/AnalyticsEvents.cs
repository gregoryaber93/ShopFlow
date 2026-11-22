using System.Text.Json;

namespace ShopFlow.Contracts.Analytics.V1;

// AZURE: these types flow through Event Hubs, not Service Bus, so they do not implement IMessage
//        and have no [MessageContract]. Event Hubs has no Subject and no server-side filters:
//        every reader gets the whole partition stream and decides on its own what it cares about.
// WHY:   a separate, looser model. Telemetry tolerates loss and duplicates of individual events,
//        so it needs no inbox, sessions or DLQ. What it needs is throughput and the ability for a
//        new consumer to re-read the whole stream.

/// <summary>Event from a browser or a mobile app. Hub <c>clickstream</c>.</summary>
public sealed record ClickstreamEvent
{
    public required Guid EventId { get; init; }

    /// <summary>
    /// Browser session identifier. Partition key in Event Hubs: events of one session land in one
    /// partition and keep their order, which makes it possible to compute the conversion funnel.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>Empty for anonymous visitors.</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>One of the <see cref="ClickstreamEventTypes"/> values.</summary>
    public required string EventType { get; init; }

    public string? Sku { get; init; }

    public string? PageUrl { get; init; }

    public string? Referrer { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

public static class ClickstreamEventTypes
{
    public const string PageView = "page_view";
    public const string ProductView = "product_view";
    public const string AddToCart = "add_to_cart";
    public const string RemoveFromCart = "remove_from_cart";
    public const string CheckoutStarted = "checkout_started";
    public const string OrderPlaced = "order_placed";
    public const string Search = "search";
}

/// <summary>
/// Integration event copied from Service Bus to the <c>business-events</c> hub.
/// The original payload is carried unchanged so that the analytics layer does not depend
/// on how quickly the forwarder picks up new versions of the contracts package.
/// </summary>
public sealed record BusinessEvent
{
    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    public required int ContractVersion { get; init; }

    public required string Source { get; init; }

    public Guid? OrderId { get; init; }

    public Guid? CustomerId { get; init; }

    public required string CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required JsonElement Payload { get; init; }
}
