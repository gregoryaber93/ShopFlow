using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Orders.Infrastructure.Cosmos;
using ShopFlow.Persistence.Cosmos;

namespace ShopFlow.Orders.Processors.Functions;

/// <summary>
/// Outbox relay: reads the Change Feed of the <c>orders</c> container and publishes outbox records to the orders-events topic.
/// </summary>
public sealed class OrdersOutboxRelayFunction(CosmosOutboxRelay relay)
{
    /// <remarks>
    /// AZURE: the most important trigger parameters:
    /// <list type="bullet">
    /// <item><c>Connection = "Cosmos"</c>: the <c>Cosmos__accountEndpoint</c>, <c>Cosmos__credential = managedidentity</c>
    /// and <c>Cosmos__clientId</c> settings, i.e. a managed identity instead of the account key.</item>
    /// <item><c>LeaseContainerName</c>: the lease container. Every physical partition of the orders container has a lease
    /// document with the read position. Leases are distributed among function instances, so the relay's parallelism
    /// grows with the number of physical partitions.</item>
    /// <item><c>LeaseContainerPrefix</c>: lets several independent readers of the same Change Feed keep their positions in
    /// one lease container without getting in each other's way.</item>
    /// <item><c>CreateLeaseContainerIfNotExists = false</c>: Bicep creates the container. The function's identity has data
    /// permissions, not permission to create containers.</item>
    /// </list>
    /// The Change Feed in its default mode does not include deletes, including those done by TTL. Expiry of old outbox
    /// records therefore does not invoke this function.
    /// <para>
    /// AZURE: <see cref="ExponentialBackoffRetryAttribute"/> with a retry count of -1 (unlimited) is mandatory here.
    /// Without a retry policy an exception in a function with a Cosmos DB trigger does not stop the feed: the lease moves
    /// the position forward and the batch of changes, i.e. the outbox events, is lost. With the policy the host retries
    /// the same batch until Service Bus accepts messages again, and later changes from that partition wait. That is
    /// exactly what we want from an outbox.
    /// </para>
    /// </remarks>
    [Function("orders-outbox-relay")]
    [ExponentialBackoffRetry(-1, "00:00:02", "00:05:00")]
    public Task Run(
        [CosmosDBTrigger(
            databaseName: OrdersContainers.DatabaseName,
            containerName: OrdersContainers.OrdersName,
            Connection = "Cosmos",
            LeaseContainerName = OrdersContainers.LeasesName,
            LeaseContainerPrefix = "outbox-relay/",
            CreateLeaseContainerIfNotExists = false,
            StartFromBeginning = false,
            MaxItemsPerInvocation = 100,
            FeedPollDelay = 1000)]
        IReadOnlyList<JsonElement> changes,
        CancellationToken cancellationToken) =>
        relay.RelayAsync(changes, OrdersContainers.OrdersName, cancellationToken);
}
