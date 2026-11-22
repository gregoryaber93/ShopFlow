using Azure.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;

namespace ShopFlow.Persistence.Cosmos;

public static class CosmosServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Cosmos DB client, the service's database and the TransactionalBatch-based outbox and inbox.
    /// Requires a registered <see cref="TokenCredential"/> and a prior call to <c>AddShopFlowMessaging</c>.
    /// </summary>
    public static IServiceCollection AddShopFlowCosmos(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CosmosOptions>()
            .Bind(configuration.GetSection(CosmosOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(CreateClient);
        services.TryAddSingleton(provider => provider
            .GetRequiredService<CosmosClient>()
            .GetDatabase(provider.GetRequiredService<IOptions<CosmosOptions>>().Value.DatabaseName));

        services.TryAddScoped<ICosmosUnitOfWorkFactory, CosmosUnitOfWorkFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFailureClassifier, CosmosFailureClassifier>());

        return services;
    }

    /// <summary>
    /// Registers the outbox relay. Only in the process that reads the Change Feed and is allowed to send to Service Bus.
    /// </summary>
    /// <remarks>
    /// WHY: writing the outbox and publishing it are two roles with different permissions. The API saves the
    /// order together with the outbox record and has no role in Service Bus at all. Only the function app with
    /// the Change Feed trigger may send to the topic. A leaked API token therefore lets nobody publish events
    /// on behalf of Orders.
    /// </remarks>
    public static IServiceCollection AddShopFlowCosmosOutboxRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<CosmosOutboxRelay>();
        return services;
    }

    private static CosmosClient CreateClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<CosmosOptions>>().Value;
        var messaging = provider.GetRequiredService<IOptions<MessagingOptions>>().Value;

        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = messaging.ServiceName,
            ConnectionMode = options.UseGatewayMode ? ConnectionMode.Gateway : ConnectionMode.Direct,
            UseSystemTextJsonSerializerWithOptions = CosmosJson.Options,
            MaxRetryAttemptsOnRateLimitedRequests = options.MaxRetryAttemptsOnRateLimitedRequests,
            MaxRetryWaitTimeOnRateLimitedRequests = options.MaxRetryWaitTimeOnRateLimitedRequests,
            EnableContentResponseOnWrite = false,
        };

        if (!string.IsNullOrWhiteSpace(options.ApplicationRegion))
        {
            clientOptions.ApplicationRegion = options.ApplicationRegion;
        }

        // AZURE: CosmosClient must be a singleton. It holds a pool of TCP connections to replicas and a cache of
        // partition addresses; every new instance starts from scratch and under load exhausts the host's
        // (SNAT) ports.
        // AZURE: the constructor with a TokenCredential means Entra ID authentication and data-plane RBAC.
        // The "Cosmos DB Built-in Data Contributor" role allows reading and writing documents but not
        // creating databases or containers. That is why there is no CreateContainerIfNotExistsAsync in the code:
        // Bicep creates the structure and the application runs with the least permissions it needs.
        return new CosmosClient(options.AccountEndpoint, provider.GetRequiredService<TokenCredential>(), clientOptions);
    }
}
