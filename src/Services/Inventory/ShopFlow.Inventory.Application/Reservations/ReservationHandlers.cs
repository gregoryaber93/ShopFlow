using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Outbox;

namespace ShopFlow.Inventory.Application.Reservations;

/// <summary>Saga step 1: reserving stock for an order.</summary>
/// <remarks>
/// Two levels of idempotency:
/// <list type="number">
/// <item>The same message (same MessageId) delivered again: the inbox in the same transaction removes it.</item>
/// <item>A different message for the same order (e.g. replayed from the DLQ): the handler finds the existing
/// reservation and sends the reply again instead of reserving twice. The saga may not have received the first
/// reply, so a reply always goes out.</item>
/// </list>
/// </remarks>
public sealed class ReserveStockHandler(
    IStockRepository stock,
    IInventoryUnitOfWork unitOfWork,
    InventoryEventPublisher publisher,
    IOutbox outbox,
    IOptions<ReservationOptions> options,
    TimeProvider timeProvider) : IMessageHandler<ReserveStock>
{
    public async Task HandleAsync(ReserveStock message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = timeProvider.GetUtcNow();

        var existing = await stock.GetReservationByOrderAsync(message.OrderId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            outbox.Enqueue(existing.Status switch
            {
                ReservationStatus.Active or ReservationStatus.Committed => InventoryEventPublisher.Reserved(existing),
                ReservationStatus.Cancelled => Failed(message, "reservation_cancelled", [], now),
                _ => Failed(message, "reservation_expired", [], now),
            });

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        ReservationLine[] lines = [.. message.Lines.Select(line => new ReservationLine(line.Sku, line.Quantity))];
        var warehouses = await stock.GetWarehousesAsync(cancellationToken).ConfigureAwait(false);
        var items = await stock.GetStockAsync([.. lines.Select(line => line.Sku)], warehouseCode: null, cancellationToken).ConfigureAwait(false);

        var outcome = Reservation.Allocate(
            Guid.CreateVersion7(now),
            message.OrderId,
            message.CustomerId,
            lines,
            warehouses,
            items,
            now,
            options.Value.TimeToLive);

        if (outcome.Reservation is { } reservation)
        {
            stock.Add(reservation);
            publisher.Publish(reservation);
        }
        else
        {
            outbox.Enqueue(Failed(message, outcome.FailureCode!, outcome.Unavailable, now));
        }

        // AZURE/SQL: one transaction: the reservation row, the changed stock levels (with rowversion checks), the reply in the
        // outbox and the inbox entry. Two concurrent reservations for the same order: the second violates the unique index on
        // OrderId, the message goes back to the queue and on retry takes the "existing" branch.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StockReservationFailed Failed(ReserveStock message, string reasonCode, IReadOnlyList<UnavailableLine> unavailable, DateTimeOffset now) => new()
    {
        OrderId = message.OrderId,
        CustomerId = message.CustomerId,
        ReasonCode = reasonCode,
        UnavailableLines = [.. unavailable.Select(line => new UnavailableLineDto { Sku = line.Sku, Requested = line.Requested, Available = line.Available })],
        FailedAt = now,
    };
}

/// <summary>Compensation for step 1. Always sends StockReleased: the saga waits for a reply even on a repeat.</summary>
public sealed class ReleaseStockHandler(
    IStockRepository stock,
    IInventoryUnitOfWork unitOfWork,
    InventoryEventPublisher publisher,
    IOutbox outbox,
    TimeProvider timeProvider) : IMessageHandler<ReleaseStock>
{
    public async Task HandleAsync(ReleaseStock message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = timeProvider.GetUtcNow();

        var reservation = await stock.GetReservationByOrderAsync(message.OrderId, cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            // Release before reservation (see Reservation.Tombstone).
            var tombstone = Reservation.Tombstone(Guid.CreateVersion7(now), message.OrderId, message.CustomerId, now);
            stock.Add(tombstone);
            publisher.Publish(tombstone);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var items = reservation.WarehouseCode is null
            ? []
            : await stock.GetStockAsync([.. reservation.Lines.Select(line => line.Sku)], reservation.WarehouseCode, cancellationToken).ConfigureAwait(false);

        reservation.Release(items, now);

        if (!publisher.Publish(reservation))
        {
            // The reservation was already released: the state does not change, but the reply must go out.
            outbox.Enqueue(InventoryEventPublisher.Released(reservation, now));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Choreography: Orders confirmed the order, so the reservation becomes a permanent stock issue.
/// </summary>
/// <remarks>
/// WHY: without this step the expiry safety net would release the stock of a confirmed order after an hour.
/// The saga does not send a "commit reservation" command, because from Inventory's point of view an order
/// confirmation is a fact, not an instruction. Inventory listens to OrderConfirmed (the orders-events/inventory
/// subscription forwarding to the inventory-commands queue).
/// </remarks>
public sealed partial class OrderConfirmedHandler(
    IStockRepository stock,
    IInventoryUnitOfWork unitOfWork,
    InventoryEventPublisher publisher,
    TimeProvider timeProvider,
    ILogger<OrderConfirmedHandler> logger) : IMessageHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // SQL has strong consistency: a missing reservation really is missing, not a replication delay
        // (unlike Cosmos DB with Session consistency). That is why it goes straight to the DLQ instead of a retry.
        var reservation = await stock.GetReservationByOrderAsync(message.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentMessageException(DeadLetterReasons.BusinessRuleViolation, $"Confirmed order {message.OrderId} has no reservation.");

        var items = await stock.GetStockAsync([.. reservation.Lines.Select(line => line.Sku)], reservation.WarehouseCode, cancellationToken).ConfigureAwait(false);
        var result = reservation.Commit(items, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            // The reservation expired before confirmation: an order was confirmed without reserved stock.
            // Needs a human decision (docs/runbooks/dlq-growth.md, section "reservation.not_active").
            LogOversellRisk(message.OrderId, reservation.Status.ToString());
            throw new PermanentMessageException(DeadLetterReasons.BusinessRuleViolation, result.Error.Message);
        }

        if (publisher.Publish(reservation))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Order {OrderId} was confirmed but its reservation is {Status}: possible oversell")]
    private partial void LogOversellRisk(Guid orderId, string status);
}

/// <summary>Expires abandoned reservations. Called by the inventory-worker background process, one reservation per transaction.</summary>
public sealed class ExpireReservationHandler(
    IStockRepository stock,
    IInventoryUnitOfWork unitOfWork,
    InventoryEventPublisher publisher,
    TimeProvider timeProvider)
{
    public Task<IReadOnlyList<Guid>> FindExpiredAsync(int max, CancellationToken cancellationToken) =>
        stock.FindExpiredReservationIdsAsync(timeProvider.GetUtcNow(), max, cancellationToken);

    /// <returns><c>true</c> if the reservation was released.</returns>
    public async Task<bool> ExpireAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await stock.GetReservationAsync(reservationId, cancellationToken).ConfigureAwait(false);
        if (reservation is null || reservation.WarehouseCode is null)
        {
            return false;
        }

        var items = await stock.GetStockAsync([.. reservation.Lines.Select(line => line.Sku)], reservation.WarehouseCode, cancellationToken).ConfigureAwait(false);
        if (!reservation.Expire(items, timeProvider.GetUtcNow()))
        {
            return false;
        }

        publisher.Publish(reservation);

        // Two worker replicas can pick the same reservation; rowversion makes the second one's write fail.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
