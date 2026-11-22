using System.Net;
using Microsoft.Azure.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;

namespace ShopFlow.Persistence.Cosmos;

public static class CosmosContainerExtensions
{
    /// <summary>
    /// Point read: the document identified by the (id, partition key) pair. Returns <c>null</c> when it does not exist.
    /// </summary>
    /// <remarks>
    /// AZURE: a point read bypasses the query engine and costs about 1 RU for a document up to 1 KB,
    /// regardless of the container size. The same thing expressed as <c>SELECT * FROM c WHERE c.id = ...</c>
    /// costs several times more, and without a partition key it becomes a query across all partitions.
    /// That is why partition keys are chosen so that the most frequent read is a point read.
    /// The returned document has its <c>_etag</c> field populated.
    /// </remarks>
    public static async Task<TDocument?> ReadOrDefaultAsync<TDocument>(
        this Container container,
        string id,
        string partitionKeyValue,
        CancellationToken cancellationToken)
        where TDocument : CosmosDocument
    {
        ArgumentNullException.ThrowIfNull(container);

        try
        {
            var response = await container
                .ReadItemAsync<TDocument>(id, new PartitionKey(partitionKeyValue), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(container.Id, "point_read", response.RequestCharge);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // AZURE: "not found" also costs RU, and the SDK reports it with an exception.
            CosmosTelemetry.RecordRequestCharge(container.Id, "point_read", exception.RequestCharge);
            return null;
        }
    }

    /// <summary>
    /// Query restricted to a single logical partition. Returns all result pages.
    /// </summary>
    /// <remarks>
    /// AZURE: query results are paged and each page is a separate request with its own RU cost.
    /// Setting <see cref="QueryRequestOptions.PartitionKey"/> guarantees that the query goes to one
    /// physical partition instead of fanning out to all of them.
    /// </remarks>
    public static async Task<IReadOnlyList<TDocument>> QuerySinglePartitionAsync<TDocument>(
        this Container container,
        QueryDefinition query,
        string partitionKeyValue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);

        var results = new List<TDocument>();
        using var iterator = container.GetItemQueryIterator<TDocument>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKeyValue) });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            CosmosTelemetry.RecordRequestCharge(container.Id, "query", page.RequestCharge);
            results.AddRange(page);
        }

        return results;
    }
}
