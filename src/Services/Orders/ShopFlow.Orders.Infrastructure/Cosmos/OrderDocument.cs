using System.Text.Json.Serialization;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.Persistence.Cosmos.Documents;

namespace ShopFlow.Orders.Infrastructure.Cosmos;

/// <summary>
/// Shape of an order in Cosmos DB.
/// </summary>
/// <remarks>
/// WHY: a separate document type instead of serializing the aggregate. A document written today will be read in a
/// few years by a newer version of the code, so its shape changes more slowly than the domain model and has its own
/// schema version (<see cref="SchemaVersion"/>).
/// <para>
/// Schema evolution through "migrate on read": version 1 had a <see cref="ShippingAddress"/> field, version 2 has
/// <see cref="Delivery"/> (pickup points). Nobody rewrites old documents in bulk. The mapper reads both shapes and
/// writes always produce version 2, so documents migrate themselves on their next change. A bulk migration of
/// millions of documents would consume the whole container's RU and, through the Change Feed, send every order to
/// the relay once again.
/// </para>
/// </remarks>
public sealed class OrderDocument : CosmosDocument
{
    public const string DocumentType = "order";
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    /// <summary>Partition key. The same value as <see cref="CosmosDocument.Id"/>.</summary>
    public required string OrderId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required OrderStatus Status { get; init; }

    public required List<OrderLineDocument> Lines { get; init; }

    public required MoneyDocument Total { get; init; }

    /// <summary>Schema version 2.</summary>
    public DeliveryDocument? Delivery { get; init; }

    /// <summary>Schema version 1. Read-only, for old documents.</summary>
    public AddressDocument? ShippingAddress { get; init; }

    public required string PaymentMethodToken { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public DateTimeOffset? ShippedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public string? TrackingNumber { get; init; }

    public string? RejectionReasonCode { get; init; }

    public string? CancellationReason { get; init; }

    public List<StatusChangeDocument> History { get; init; } = [];
}

public sealed record OrderLineDocument(string Sku, string ProductName, int Quantity, MoneyDocument UnitPrice);

public sealed record MoneyDocument(decimal Amount, string Currency);

public sealed record DeliveryDocument(DeliveryMethod Method, AddressDocument? Address, string? PickupPointId);

public sealed record AddressDocument(
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string CountryCode,
    string? Phone);

public sealed record StatusChangeDocument(OrderStatus Status, DateTimeOffset At, string? Note);
