using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Application.Orders;

/// <summary>Reads an order by identifier.</summary>
/// <remarks>
/// WHY: orders-api only answers "show order X" (a point read by partition key). Customer order lists are served
/// by readmodels-api from a projection partitioned by customer. The same list from the write model would be a
/// query across all partitions of the orders container.
/// </remarks>
public sealed class GetOrderHandler(IOrderRepository orders)
{
    public async Task<Result<Order>> HandleAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(orderId, cancellationToken).ConfigureAwait(false);

        return order is null || (customerId is { } owner && order.CustomerId != owner)
            ? OrderErrors.NotFound
            : order;
    }
}

/// <summary>The customer asks to cancel the order (POST /v1/orders/{id}/cancellation with If-Match).</summary>
public sealed class RequestCancellationHandler(OrderCommandExecutor executor, TimeProvider timeProvider)
{
    public Task<Result<OrderMutation>> HandleAsync(
        Guid orderId,
        Guid customerId,
        string ifMatch,
        string? reason,
        CancellationToken cancellationToken) =>
        executor.ExecuteAsync(
            orderId,
            customerId,
            ifMatch,
            order => order.RequestCancellation("customer", reason, timeProvider.GetUtcNow()),
            cancellationToken);
}

/// <summary>
/// Operations called by order-saga over HTTP (/internal/orders/...).
/// </summary>
/// <remarks>
/// WHY: the saga calls these operations synchronously because it needs an immediate, version-checked result:
/// "confirm, unless someone asked for cancellation in the meantime". With a command over a queue this
/// information would come back as another event and the saga would have to handle an extra state.
/// Every operation is idempotent: after a successful call with a lost response the saga retries, gets 412 (the
/// ETag changed), reads the order and sees that the target state has been reached.
/// </remarks>
public sealed class SagaOrderOperations(OrderCommandExecutor executor, TimeProvider timeProvider)
{
    public Task<Result<OrderMutation>> ConfirmAsync(Guid orderId, string ifMatch, string? trackingNumber, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(orderId, customerId: null, ifMatch, order => order.Confirm(trackingNumber, timeProvider.GetUtcNow()), cancellationToken);

    public Task<Result<OrderMutation>> RejectAsync(Guid orderId, string ifMatch, string reasonCode, string? description, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return Task.FromResult<Result<OrderMutation>>(Error.Validation("order.reason_required", "A rejection reason code is required."));
        }

        // We also accept a code outside OrderRejectionReasons: it is a string, not an enum, precisely so that
        // the saga can add a new reason without deploying Orders at the same time.
        return executor.ExecuteAsync(orderId, customerId: null, ifMatch, order => order.Reject(reasonCode, description, timeProvider.GetUtcNow()), cancellationToken);
    }

    public Task<Result<OrderMutation>> CompleteCancellationAsync(Guid orderId, string ifMatch, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(orderId, customerId: null, ifMatch, order => order.CompleteCancellation(timeProvider.GetUtcNow()), cancellationToken);
}
