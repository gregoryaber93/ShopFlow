using Microsoft.Azure.Cosmos;

namespace ShopFlow.Orders.Infrastructure.Cosmos;

/// <summary>
/// Containers of the <c>orders</c> database. Names and partition keys must match <c>infra/layers/data.bicep</c>,
/// because the application is not allowed to create containers.
/// </summary>
/// <remarks>
/// AZURE: a partition key cannot be changed after the container is created. Changing it means a new container and a
/// data migration (e.g. copying through the Change Feed), which is why the choice is documented in docs/adr/0007.
/// </remarks>
public sealed class OrdersContainers(Database database)
{
    /// <summary>The Orders database on the environment's shared Cosmos DB account. A constant, because trigger attributes use it.</summary>
    public const string DatabaseName = "orders";

    public const string OrdersName = "orders";
    public const string OrdersPartitionKey = "orderId";

    public const string CartsName = "carts";
    public const string CartsPartitionKey = "customerId";

    public const string IdempotencyName = "idempotency";
    public const string IdempotencyPartitionKey = "scopeKey";

    public const string LeasesName = "leases";

    /// <summary>Partition = order. The aggregate, its outbox and inbox live together, so they are saved in one batch.</summary>
    public Container Orders { get; } = database.GetContainer(OrdersName);

    /// <summary>Partition = customer. One cart per customer, a point read by the customer identifier.</summary>
    public Container Carts { get; } = database.GetContainer(CartsName);

    /// <summary>Partition = (customer, idempotency key). Even distribution; every key is a separate logical partition.</summary>
    public Container Idempotency { get; } = database.GetContainer(IdempotencyName);
}
