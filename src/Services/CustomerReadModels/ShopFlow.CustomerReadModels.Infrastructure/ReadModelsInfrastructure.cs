using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Azure.Redis;
using ShopFlow.Azure.Storage;
using ShopFlow.Contracts;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.Messaging;
using ShopFlow.Observability;
using ShopFlow.Persistence.Cosmos;
using StackExchange.Redis;

namespace ShopFlow.CustomerReadModels.Infrastructure;

public sealed class OrderDetailsCacheOptions
{
    public const string SectionName = "OrderDetailsCache";

    /// <summary>
    /// Safety net in case invalidation fails.
    /// </summary>
    /// <remarks>
    /// WHY: the projector removes the key after every change, so the TTL does not decide freshness in the normal case. It exists
    /// for two situations: the invalidation failed (Redis briefly unavailable), or the API read the old version from the database
    /// right before the projector's write and put it into the cache right after the invalidation. In both cases stale data lives
    /// at most as long as the TTL.
    /// </remarks>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Order details in Azure Managed Redis under the <c>rm:order:{orderId}</c> key.
/// </summary>
/// <remarks>
/// WHY: Redis instead of a Cosmos point read (1 RU, a few ms), because after placing an order the customer application polls
/// the details every few seconds, waiting for the confirmation. At peak that is thousands of reads per second of the same
/// documents. The cache together with the ETag allows answering 304 without touching the database.
/// WHY: no L1 layer in process memory (unlike the catalog in Inventory). Invalidation comes from another process (the
/// projector), and a local copy in every API replica cannot be removed from outside. A customer who has just paid should not
/// see "awaiting payment" for 30 seconds.
/// </remarks>
internal sealed partial class RedisOrderDetailsCache(
    IDistributedCache cache,
    IOptions<OrderDetailsCacheOptions> options,
    ILogger<RedisOrderDetailsCache> logger) : IOrderDetailsCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CustomerOrderView?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await cache.GetAsync(Key(orderId), cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : JsonSerializer.Deserialize<CustomerOrderView>(bytes, Json);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or JsonException)
        {
            LogCacheFailure(exception, "read", orderId);
            return null;
        }
    }

    public async Task SetAsync(CustomerOrderView view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);

        try
        {
            await cache.SetAsync(
                    Key(view.OrderId),
                    JsonSerializer.SerializeToUtf8Bytes(view, Json),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.TimeToLive },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            LogCacheFailure(exception, "write", view.OrderId);
        }
    }

    public async Task InvalidateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(Key(orderId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            // WHY: the projection does not depend on cache availability. The view is already saved in Cosmos, and a stale entry expires
            // after the TTL. An exception would hand the message back to the queue, and the retry would end at "already applied" without
            // another invalidation attempt anyway.
            LogCacheFailure(exception, "invalidate", orderId);
        }
    }

    // The "rm:" prefix is added by AddShopFlowRedis (InstanceName), so the key in Redis is rm:order:{orderId}.
    private static string Key(Guid orderId) => $"order:{orderId:D}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order details cache {Operation} failed for order {OrderId}")]
    private partial void LogCacheFailure(Exception exception, string operation, Guid orderId);
}

public sealed record InvoiceLink(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Short-lived invoice link for a signed-in customer.</summary>
public interface IInvoiceDownloadLinks
{
    Task<InvoiceLink> CreateAsync(string blobPath, CancellationToken cancellationToken);
}

public sealed class InvoiceDownloadOptions
{
    public const string SectionName = "InvoiceDownloads";

    public Uri? ServiceUri { get; set; }

    public string ContainerName { get; set; } = "invoices";

    /// <summary>
    /// WHY: a few minutes, not 72 hours as in the e-mail. The link is created when a signed-in customer clicks and is used by the
    /// browser right away, so there is no reason for it to live longer.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
}

internal sealed class BlobInvoiceDownloadLinks(BlobReadLinkFactory links, IOptions<InvoiceDownloadOptions> options, TimeProvider timeProvider) : IInvoiceDownloadLinks
{
    public async Task<InvoiceLink> CreateAsync(string blobPath, CancellationToken cancellationToken)
    {
        var expiresAt = timeProvider.GetUtcNow() + options.Value.Lifetime;
        var url = await links.CreateReadLinkAsync(options.Value.ContainerName, blobPath, options.Value.Lifetime, cancellationToken).ConfigureAwait(false);
        return new InvoiceLink(url, expiresAt);
    }
}

public static class CustomerReadModelsInfrastructureExtensions
{
    /// <summary>The projector only consumes: no publishing routes, no outbox.</summary>
    public static MessagingBuilder AddCustomerReadModelsMessaging(this IServiceCollection services) =>
        services.AddShopFlowMessaging(MessagingTopology.Consumers.ReadModels);

    /// <summary>Order views in Cosmos DB (the <c>readmodels</c> database).</summary>
    /// <param name="applicationName">Process name in the Cosmos client's User-Agent header (diagnostics on the account side).</param>
    public static IServiceCollection AddCustomerOrderStore(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // CosmosClient takes the application name from MessagingOptions; the API does not register messaging, so we set it here.
        services.AddOptions<MessagingOptions>().Configure(options => options.ServiceName = applicationName);
        services.AddShopFlowCosmos(configuration);
        services.TryAddSingleton<ICustomerOrderStore, CosmosCustomerOrderStore>();

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "cosmos-customer-orders",
            provider => new CosmosContainerHealthCheck(provider.GetRequiredService<Database>().GetContainer(ReadModelsContainers.CustomerOrdersName)),
            HealthStatus.Unhealthy,
            [HealthTags.Dependencies]));

        return services;
    }

    /// <summary>Details cache in Redis. The projector invalidates it, the API reads and fills it.</summary>
    public static IServiceCollection AddOrderDetailsCache(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OrderDetailsCacheOptions>().Bind(configuration.GetSection(OrderDetailsCacheOptions.SectionName));
        services.AddShopFlowRedis(configuration, keyPrefix: "rm:");
        services.TryAddSingleton<IOrderDetailsCache, RedisOrderDetailsCache>();

        return services;
    }

    /// <summary>Invoice links (user delegation SAS through Front Door). API only.</summary>
    public static IServiceCollection AddInvoiceDownloads(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // AZURE: the readmodels-api identity has Storage Blob Data Reader on the invoices container and Storage Blob Delegator on the
        // account. This is the API's only Storage permission; it writes no files.
        var section = configuration.GetSection(InvoiceDownloadOptions.SectionName);
        services.AddOptions<InvoiceDownloadOptions>().Bind(section);
        services.AddOptions<BlobLinkOptions>().Bind(configuration.GetSection(BlobLinkOptions.SectionName));
        services.AddAzureClients(clients => clients.AddBlobServiceClient(section));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BlobReadLinkFactory>();
        services.TryAddSingleton<IInvoiceDownloadLinks, BlobInvoiceDownloadLinks>();

        return services;
    }
}
