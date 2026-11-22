using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.Persistence.Cosmos;

namespace ShopFlow.Orders.Infrastructure.Cosmos;

internal sealed class CosmosOrderRepository(OrdersContainers containers, ICosmosUnitOfWorkFactory unitOfWork) : IOrderRepository
{
    public async Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var key = orderId.ToString("D");

        // AZURE: a point read (id + partition key), about 1 RU for a document up to 1 KB.
        // The document identifier and the partition key are the same value, so nothing beyond the order
        // identifier from the URL is needed.
        var document = await containers.Orders
            .ReadOrDefaultAsync<OrderDocument>(key, key, cancellationToken)
            .ConfigureAwait(false);

        return document is null ? null : OrderDocumentMapper.ToDomain(document);
    }

    public async Task<string> AddAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var document = OrderDocumentMapper.ToDocument(order);
        var result = await unitOfWork
            .Create(containers.Orders, OrdersContainers.OrdersPartitionKey, document.OrderId)
            .Create(document)
            .CommitAsync(cancellationToken)
            .ConfigureAwait(false);

        return Track(order, result.GetETag(document.Id));
    }

    public async Task<string> UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var expected = order.ConcurrencyToken
            ?? throw new InvalidOperationException($"Order {order.Id} was not loaded from the store; it has no concurrency token.");

        var document = OrderDocumentMapper.ToDocument(order);
        var result = await unitOfWork
            .Create(containers.Orders, OrdersContainers.OrdersPartitionKey, document.OrderId)
            .Replace(document, expected)
            .CommitAsync(cancellationToken)
            .ConfigureAwait(false);

        return Track(order, result.GetETag(document.Id));
    }

    private static string Track(Order order, string? etag)
    {
        var value = etag ?? throw new InvalidOperationException("Cosmos DB did not return an ETag for the written order.");
        order.SetConcurrencyToken(value);
        return value;
    }
}
