using Microsoft.Extensions.Logging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Shipping.Domain;
using V1 = ShopFlow.Contracts.Shipping.V1;
using V2 = ShopFlow.Contracts.Shipping.V2;

namespace ShopFlow.Shipping.Application;

/// <summary>
/// Saga step 3. Handles both versions of the CreateShipment contract by reducing them to one request.
/// </summary>
/// <remarks>
/// WHY: two contract versions, one piece of logic. The saga sends v1 for couriers and v2 for pickup points, so Shipping
/// must understand both until v1 is retired. Translating at the entry point means the rest of the code does not know
/// versions exist, and removing v1 means removing one method and one handler registration.
/// </remarks>
public sealed class CreateShipmentHandler(
    IShipmentRepository shipments,
    ICarrierGateway carrier,
    ILabelStorage labels,
    ShipmentEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<V1.CreateShipment>, IMessageHandler<V2.CreateShipment>
{
    public Task HandleAsync(V1.CreateShipment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var address = message.Destination;

        return CreateAsync(
            message.ShipmentId ?? DeterministicGuid.Create($"shipment/{message.OrderId:D}"),
            message.OrderId,
            message.CustomerId,
            message.OrderNumber,
            message.WarehouseCode,
            new ShipmentDestination(false, null, address.RecipientName, address.Line1, address.Line2, address.City, address.PostalCode, address.CountryCode, address.Phone),
            [.. message.Lines.Select(line => new ParcelLine(line.Sku, line.Quantity))],
            cancellationToken);
    }

    public Task HandleAsync(V2.CreateShipment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var destination = message.Destination;
        var address = destination.Address;

        return CreateAsync(
            message.ShipmentId,
            message.OrderId,
            message.CustomerId,
            message.OrderNumber,
            message.WarehouseCode,
            new ShipmentDestination(
                destination.Method == V2.ShipmentDeliveryMethod.PickupPoint,
                destination.PickupPointId,
                address?.RecipientName,
                address?.Line1,
                address?.Line2,
                address?.City,
                address?.PostalCode,
                address?.CountryCode,
                address?.Phone),
            [.. message.Lines.Select(line => new ParcelLine(line.Sku, line.Quantity))],
            cancellationToken);
    }

    private async Task CreateAsync(
        Guid shipmentId,
        Guid orderId,
        Guid customerId,
        string orderNumber,
        string warehouseCode,
        ShipmentDestination destination,
        IReadOnlyList<ParcelLine> lines,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var existing = await shipments.GetAsync(orderId, shipmentId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // A repeated command: a reply consistent with the state (tombstone → refusal, otherwise "created").
            outbox.Enqueue(existing.Status == ShipmentStatus.Cancelled && existing.TrackingNumber is null
                ? Replies.CreationFailed(orderId, customerId, "shipment_cancelled", "The shipment was cancelled before creation.", now)
                : Replies.Created(existing));
            await shipments.SaveAsync(existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Idempotency key at the carrier = shipment identifier (see CarrierGatewayClient).
        var parcel = await carrier
            .CreateParcelAsync(new ParcelRequest(shipmentId, orderId, orderNumber, warehouseCode, destination, lines), cancellationToken)
            .ConfigureAwait(false);

        if (!parcel.Succeeded)
        {
            outbox.Enqueue(Replies.CreationFailed(orderId, customerId, parcel.FailureCode!, parcel.FailureDescription, now));
            await shipments.CommitWithoutShipmentAsync(orderId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var labelPath = await labels.UploadAsync(orderId, shipmentId, parcel.LabelPdf!, cancellationToken).ConfigureAwait(false);
        var shipment = Shipment.Create(shipmentId, orderId, customerId, orderNumber, warehouseCode, destination, carrier.CarrierCode, parcel.TrackingNumber!, labelPath, now);

        publisher.Publish(shipment);
        await shipments.SaveAsync(shipment, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Compensation for step 3. Always replies with ShipmentCancelled.</summary>
public sealed class CancelShipmentHandler(
    IShipmentRepository shipments,
    ICarrierGateway carrier,
    ShipmentEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<V1.CancelShipment>
{
    public async Task HandleAsync(V1.CancelShipment message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = timeProvider.GetUtcNow();

        var shipment = await shipments.GetAsync(message.OrderId, message.ShipmentId, cancellationToken).ConfigureAwait(false)
            ?? Shipment.CancelledTombstone(message.ShipmentId, message.OrderId, message.CustomerId, now);

        if (shipment.Status == ShipmentStatus.Created && shipment.TrackingNumber is { } trackingNumber
            && !await carrier.CancelParcelAsync(shipment.Id, trackingNumber, cancellationToken).ConfigureAwait(false))
        {
            throw new PermanentMessageException(DeadLetterReasons.BusinessRuleViolation, $"Carrier refused to cancel parcel {trackingNumber}: already picked up.");
        }

        var result = shipment.Cancel(now);
        if (result.IsFailure)
        {
            // The parcel is already on its way: the compensation needs a human decision (return to sender).
            throw new PermanentMessageException(DeadLetterReasons.BusinessRuleViolation, $"{result.Error.Code}: shipment {shipment.Id}");
        }

        if (!publisher.Publish(shipment).Contains(nameof(V1.ShipmentCancelled)))
        {
            outbox.Enqueue(Replies.Cancelled(shipment, now));
        }

        await shipments.SaveAsync(shipment, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record CarrierEvent(string EventId, string Type, Guid OrderId, Guid ShipmentId, DateTimeOffset OccurredAt, DateOnly? EstimatedDeliveryDate);

public enum CarrierEventOutcome
{
    Processed = 0,
    Ignored = 1,
    RetryLater = 2,
}

/// <summary>Carrier events (dispatch, delivery) → ShipmentDispatched / ShipmentDelivered.</summary>
public sealed partial class CarrierWebhookProcessor(
    IShipmentRepository shipments,
    ShipmentEventPublisher publisher,
    ILogger<CarrierWebhookProcessor> logger)
{
    public async Task<CarrierEventOutcome> ProcessAsync(CarrierEvent carrierEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrierEvent);

        var shipment = await shipments.GetAsync(carrierEvent.OrderId, carrierEvent.ShipmentId, cancellationToken).ConfigureAwait(false);
        if (shipment is null)
        {
            return CarrierEventOutcome.RetryLater;
        }

        var result = carrierEvent.Type switch
        {
            "parcel.dispatched" => shipment.MarkDispatched(carrierEvent.OccurredAt, carrierEvent.EstimatedDeliveryDate),
            "parcel.delivered" => shipment.MarkDelivered(carrierEvent.OccurredAt),
            _ => null,
        };

        if (result is null)
        {
            return CarrierEventOutcome.Ignored;
        }

        if (result.IsFailure)
        {
            // A physical fact (parcel picked up / delivered) cannot be refused. We accept the event so that the carrier does not
            // retry it, and raise an alarm: a cancelled shipment left the warehouse.
            LogInconsistency(shipment.Id, carrierEvent.Type, result.Error.Code);
            return CarrierEventOutcome.Processed;
        }

        publisher.Publish(shipment);
        await shipments.SaveAsync(shipment, cancellationToken).ConfigureAwait(false);
        return CarrierEventOutcome.Processed;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Carrier event {Type} for shipment {ShipmentId} conflicts with its state: {Code}")]
    private partial void LogInconsistency(Guid shipmentId, string type, string code);
}
