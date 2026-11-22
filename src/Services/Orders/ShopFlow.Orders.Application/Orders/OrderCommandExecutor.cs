using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Persistence;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Application.Orders;

/// <summary>The order after the operation and its current ETag (for the response header).</summary>
public sealed record OrderMutation(Order Order, string ETag);

/// <summary>
/// Common flow of every change to an existing order: read, access and version check, domain operation,
/// event publishing, conditional write.
/// </summary>
public sealed class OrderCommandExecutor(IOrderRepository orders, OrderEventPublisher publisher)
{
    /// <param name="orderId">The order.</param>
    /// <param name="customerId">The owner when a customer calls; <c>null</c> for internal calls (the saga).</param>
    /// <param name="expectedETag">ETag from the If-Match header, or <c>null</c> when the service controls the version itself.</param>
    /// <param name="operation">Operation on the aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result<OrderMutation>> ExecuteAsync(
        Guid orderId,
        Guid? customerId,
        string? expectedETag,
        Func<Order, Result> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var order = await orders.GetAsync(orderId, cancellationToken).ConfigureAwait(false);

        // WHY: someone else's order is a 404, not a 403. A 403 would confirm that an order with this
        // identifier exists.
        if (order is null || (customerId is { } owner && order.CustomerId != owner))
        {
            return OrderErrors.NotFound;
        }

        // An early check saves the domain logic and the write when the client has a stale version.
        // It does not replace the check in the database: someone may change the order between this read and the write,
        // and then Cosmos rejects the If-Match write with 412.
        if (expectedETag is not null && !string.Equals(expectedETag, order.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException(nameof(Order), orderId.ToString("D"));
        }

        var result = operation(order);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // An idempotent operation repeated in the target state: no events, no write, the same ETag.
        if (!await publisher.PublishAsync(order).ConfigureAwait(false))
        {
            return new OrderMutation(order, order.ConcurrencyToken ?? string.Empty);
        }

        var etag = await orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        return new OrderMutation(order, etag);
    }
}
