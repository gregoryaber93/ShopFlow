using System.Buffers.Text;
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.CustomerReadModels.Infrastructure;

public static class ReadModelsContainers
{
    public const string DatabaseName = "readmodels";

    /// <summary>
    /// Order views partitioned by <c>/customerId</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: this is the actual reason this projection exists. The write model (the <c>orders</c> container) is partitioned by
    /// <c>orderId</c>, so "a customer's orders" would be a query across all partitions there. Here it is a query in one logical
    /// partition, and order details are a point read (id + key). The logical partition limit (20 GB) is not a problem: even a
    /// very active customer has hundreds of orders of a few KB each.
    /// </remarks>
    public const string CustomerOrdersName = "customer-orders";

    public const string PartitionKey = "customerId";
}

internal sealed class CosmosCustomerOrderStore(Database database) : ICustomerOrderStore
{
    private readonly Container _container = database.GetContainer(ReadModelsContainers.CustomerOrdersName);

    public async Task<CustomerOrderView?> GetAsync(Guid customerId, Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container
                .ReadItemAsync<CustomerOrderView>(orderId.ToString("D"), new PartitionKey(customerId.ToString("D")), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, "point_read", response.RequestCharge);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // AZURE: reading a document from someone else's partition also ends with 404. The partition key comes from the signed-in
            // customer's token, so the query itself is the authorization: you cannot "hit" someone else's order.
            CosmosTelemetry.RecordRequestCharge(_container.Id, "point_read", exception.RequestCharge);
            return null;
        }
    }

    public async Task SaveAsync(CustomerOrderView view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);

        var partitionKey = new PartitionKey(view.CustomerId);
        var options = new ItemRequestOptions { EnableContentResponseOnWrite = false, IfMatchEtag = view.ETag };

        try
        {
            var response = view.ETag is null
                ? await _container.CreateItemAsync(view, partitionKey, options, cancellationToken).ConfigureAwait(false)
                : await _container.ReplaceItemAsync(view, view.Id, partitionKey, options, cancellationToken).ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, view.ETag is null ? "projection_create" : "projection_replace", response.RequestCharge);
            view.ETag = response.ETag;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            // Another projection run (DLQ replay, rebuild) saved the document in the meantime. The exception is transient: the message
            // retry reads the fresh document and applies the event once more.
            throw new ConcurrencyConflictException("CustomerOrderView", view.Id, exception);
        }
    }

    public async Task<CustomerOrderPage> ListAsync(Guid customerId, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                "SELECT c.orderId, c.orderNumber, c.status, c.submittedAt, c.total, ARRAY_LENGTH(c.lines) AS itemCount " +
                "FROM c WHERE c.docType = @docType AND IS_STRING(c.orderNumber) ORDER BY c.submittedAt DESC")
            .WithParameter("@docType", CustomerOrderView.DocumentType);

        // AZURE: paging with a continuation token, not OFFSET/LIMIT. OFFSET in Cosmos DB reads and discards every skipped document,
        // so the tenth page costs ten times as many RU as the first. The token points at where the previous page ended, so every page
        // costs the same. The token size limit keeps it from growing beyond a reasonable URL parameter length.
        using var iterator = _container.GetItemQueryIterator<CustomerOrderSummary>(
            query,
            DecodeToken(continuationToken),
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(customerId.ToString("D")),
                MaxItemCount = pageSize,
                ResponseContinuationTokenLimitInKb = 2,
            });

        var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        CosmosTelemetry.RecordRequestCharge(_container.Id, "query", page.RequestCharge);

        return new CustomerOrderPage([.. page], EncodeToken(page.ContinuationToken));
    }

    // WHY: the Cosmos token is JSON with quotes and brackets. Base64url lets us give it to the client as a plain query parameter,
    // and the client must treat it as opaque anyway.
    internal static string? EncodeToken(string? token) =>
        token is null ? null : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));

    internal static string? DecodeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The continuation token is not valid.", nameof(token), exception);
        }
    }
}
