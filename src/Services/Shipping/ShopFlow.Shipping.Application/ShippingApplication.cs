using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Shipping.Domain;

namespace ShopFlow.Shipping.Application;

public interface IShipmentRepository
{
    Task<Shipment?> GetAsync(Guid orderId, Guid shipmentId, CancellationToken cancellationToken);

    Task SaveAsync(Shipment shipment, CancellationToken cancellationToken);

    /// <summary>
    /// Commits only the reply (outbox) and the inbox entry, without a shipment document.
    /// Needed when the shipment was not created (the carrier refused) but the saga still has to get a reply.
    /// </summary>
    Task CommitWithoutShipmentAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed record ParcelLine(string Sku, int Quantity);

public sealed record ParcelRequest(Guid ShipmentId, Guid OrderId, string OrderNumber, string WarehouseCode, ShipmentDestination Destination, IReadOnlyList<ParcelLine> Lines);

public sealed record ParcelResult(bool Succeeded, string? TrackingNumber, byte[]? LabelPdf, string? FailureCode, string? FailureDescription)
{
    public static ParcelResult Created(string trackingNumber, byte[] labelPdf) => new(true, trackingNumber, labelPdf, null, null);

    public static ParcelResult Refused(string code, string? description) => new(false, null, null, code, description);
}

/// <summary>Port to the carrier API. A business refusal is a result; an exception means a technical error (retry).</summary>
public interface ICarrierGateway
{
    string CarrierCode { get; }

    Task<ParcelResult> CreateParcelAsync(ParcelRequest request, CancellationToken cancellationToken);

    /// <returns><c>false</c> when the carrier has already picked up the parcel and cancellation is impossible.</returns>
    Task<bool> CancelParcelAsync(Guid shipmentId, string trackingNumber, CancellationToken cancellationToken);
}

public interface ILabelStorage
{
    /// <returns>Blob path relative to the labels container.</returns>
    Task<string> UploadAsync(Guid orderId, Guid shipmentId, byte[] labelPdf, CancellationToken cancellationToken);
}

/// <summary>Shipment domain events → contracts in the outbox.</summary>
public sealed class ShipmentEventPublisher(IOutbox outbox)
{
    public IReadOnlySet<string> Publish(Shipment shipment)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        var published = new HashSet<string>(StringComparer.Ordinal);

        foreach (var domainEvent in shipment.DequeueDomainEvents())
        {
            IMessage message = domainEvent switch
            {
                ShipmentCreatedDomainEvent => Replies.Created(shipment),
                ShipmentCancelledDomainEvent cancelled => Replies.Cancelled(shipment, cancelled.OccurredAt),
                ShipmentDispatchedDomainEvent dispatched => new ShipmentDispatched
                {
                    OrderId = shipment.OrderId,
                    CustomerId = shipment.CustomerId,
                    ShipmentId = shipment.Id,
                    CarrierCode = shipment.CarrierCode!,
                    TrackingNumber = shipment.TrackingNumber!,
                    DispatchedAt = dispatched.OccurredAt,
                    EstimatedDeliveryDate = dispatched.EstimatedDeliveryDate,
                },
                ShipmentDeliveredDomainEvent delivered => new ShipmentDelivered
                {
                    OrderId = shipment.OrderId,
                    CustomerId = shipment.CustomerId,
                    ShipmentId = shipment.Id,
                    DeliveredAt = delivered.OccurredAt,
                },
                _ => throw new InvalidOperationException($"Domain event {domainEvent.GetType().Name} has no integration mapping."),
            };

            outbox.Enqueue(message);
            published.Add(message.GetType().Name);
        }

        return published;
    }
}

public static class Replies
{
    public static ShipmentCreated Created(Shipment shipment) => new()
    {
        OrderId = shipment.OrderId,
        CustomerId = shipment.CustomerId,
        ShipmentId = shipment.Id,
        CarrierCode = shipment.CarrierCode!,
        TrackingNumber = shipment.TrackingNumber!,
        LabelBlobPath = shipment.LabelBlobPath!,
        CreatedAt = shipment.CreatedAt,
    };

    public static ShipmentCancelled Cancelled(Shipment shipment, DateTimeOffset at) => new()
    {
        OrderId = shipment.OrderId,
        CustomerId = shipment.CustomerId,
        ShipmentId = shipment.Id,
        CancelledAt = at,
    };

    public static ShipmentCreationFailed CreationFailed(Guid orderId, Guid customerId, string reasonCode, string? description, DateTimeOffset at) => new()
    {
        OrderId = orderId,
        CustomerId = customerId,
        ReasonCode = reasonCode,
        ReasonDescription = description,
        FailedAt = at,
    };
}

public static class ShippingApplicationExtensions
{
    public static IServiceCollection AddShippingApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ShipmentEventPublisher>();
        services.AddScoped<CarrierWebhookProcessor>();
        return services;
    }
}
