using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopFlow.Azure.Redis;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Observability;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Infrastructure.Catalog;
using ShopFlow.Orders.Infrastructure.Cosmos;
using ShopFlow.Orders.Infrastructure.Support;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Resilience;
using V1 = ShopFlow.Contracts.Orders.V1;
using V2 = ShopFlow.Contracts.Orders.V2;

namespace ShopFlow.Orders.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Shared by orders-api and orders-processors: Cosmos DB, repositories, flags.</summary>
    public static IServiceCollection AddOrdersInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddShopFlowCosmos(configuration);
        services.AddSingleton<OrdersContainers>();
        services.AddScoped<IOrderRepository, CosmosOrderRepository>();
        services.AddScoped<ICartRepository, CosmosCartRepository>();
        services.AddSingleton<IOrderNumberGenerator, OrderNumberGenerator>();
        services.AddSingleton<IOrderFeatures, OrderFeatures>();

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "cosmos-orders",
            provider => new CosmosContainerHealthCheck(provider.GetRequiredService<OrdersContainers>().Orders),
            failureStatus: HealthStatus.Unhealthy,
            tags: [HealthTags.Dependencies]));

        return services;
    }

    /// <summary>
    /// Orders event routes. Shared by both processes: the API puts events from HTTP requests into the outbox,
    /// and processors put OrderCompleted there when handling shipping events.
    /// </summary>
    public static MessagingBuilder AddOrdersMessaging(this IServiceCollection services) =>
        services.AddShopFlowMessaging(MessagingTopology.Sources.Orders)
            .Route<V1.OrderSubmitted>(MessagingTopology.Topics.OrdersEvents)
            .Route<V2.OrderSubmitted>(MessagingTopology.Topics.OrdersEvents)
            .Route<V1.OrderConfirmed>(MessagingTopology.Topics.OrdersEvents)
            .Route<V1.OrderRejected>(MessagingTopology.Topics.OrdersEvents)
            .Route<V1.OrderCancellationRequested>(MessagingTopology.Topics.OrdersEvents)
            .Route<V1.OrderCancelled>(MessagingTopology.Topics.OrdersEvents)
            .Route<V1.OrderCompleted>(MessagingTopology.Topics.OrdersEvents);

    /// <summary>Catalog client with resilience, a service-to-service token and a Redis fallback. orders-api only.</summary>
    public static IServiceCollection AddCatalogPricing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddShopFlowRedis(configuration, keyPrefix: "orders:");

        var catalog = configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>() ?? new CatalogOptions();

        var client = services.AddHttpClient<ICatalogPricing, CatalogPricingClient>(http =>
        {
            http.BaseAddress = catalog.InventoryApiBaseUrl;

            // The resilience pipeline sets the total limit; HttpClient's default 100 s would only get in the way.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Order matters: a handler added later sits closer to the network, so the token is acquired on every
        // attempt (inside the retry), not once before all attempts.
        client.AddInternalServiceResilience();
        client.AddServiceToServiceAuthentication(catalog.Scope);

        return services;
    }
}
