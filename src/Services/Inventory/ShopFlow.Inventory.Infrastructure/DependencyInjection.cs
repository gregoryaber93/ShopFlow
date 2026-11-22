using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Azure.Redis;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Infrastructure.Catalog;
using ShopFlow.Inventory.Infrastructure.Persistence;
using ShopFlow.Messaging;
using ShopFlow.Observability;
using ShopFlow.Persistence.SqlServer;

namespace ShopFlow.Inventory.Infrastructure;

public static class DependencyInjection
{
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration (Sql, Sql:Outbox sections).</param>
    /// <param name="runOutboxPoller">
    /// <c>true</c> only in inventory-worker. The API writes the outbox (price changes), but only the worker publishes,
    /// so only its identity has the Data Sender role on the inventory-events topic.
    /// </param>
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services, IConfiguration configuration, bool runOutboxPoller)
    {
        services.AddShopFlowSqlServer<InventoryDbContext>(configuration, runOutboxPoller);

        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IInventoryUnitOfWork, InventoryUnitOfWork>();
        services.AddScoped<SqlCatalogReader>();
        services.TryAddScoped<ICatalogReader>(provider => provider.GetRequiredService<SqlCatalogReader>());

        services.AddHealthChecks().AddDbContextCheck<InventoryDbContext>("sql-inventory", tags: [HealthTags.Dependencies]);

        return services;
    }

    /// <summary>Catalog with HybridCache (L1 in process + L2 in Azure Managed Redis). inventory-api only.</summary>
    public static IServiceCollection AddCatalogCache(this IServiceCollection services, IConfiguration configuration)
    {
        // HybridCache itself uses the registered IDistributedCache (Redis here) as the L2 level.
        services.AddShopFlowRedis(configuration, keyPrefix: "inventory:");
        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024;
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5),
                LocalCacheExpiration = TimeSpan.FromSeconds(30),
            };
        });

        services.Replace(ServiceDescriptor.Scoped<ICatalogReader, CachedCatalogReader>());
        return services;
    }

    public static MessagingBuilder AddInventoryMessaging(this IServiceCollection services) =>
        services.AddShopFlowMessaging(MessagingTopology.Sources.Inventory)
            .Route<StockReserved>(MessagingTopology.Topics.InventoryEvents)
            .Route<StockReservationFailed>(MessagingTopology.Topics.InventoryEvents)
            .Route<StockReleased>(MessagingTopology.Topics.InventoryEvents)
            .Route<StockLevelLow>(MessagingTopology.Topics.InventoryEvents)
            .Route<ProductPriceChanged>(MessagingTopology.Topics.InventoryEvents);
}
