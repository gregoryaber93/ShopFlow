using Azure.Core;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopFlow.Analytics.Application;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Observability;
using ShopFlow.Persistence.Cosmos;

namespace ShopFlow.Analytics.Infrastructure;

public static class AnalyticsInfrastructureExtensions
{
    public static IServiceCollection AddAnalyticsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EventHubsOptions>()
            .Bind(configuration.GetSection(EventHubsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AZURE: one producer per hub and per process. The client holds an AMQP connection and is thread-safe; creating it per
        // function invocation means a new TLS connection and a new token for every event.
        // The managed identity has the Azure Event Hubs Data Sender role on the specific hub, not on the namespace.
        services.TryAddSingleton(provider => new HubProducer<ClickstreamHub>(CreateProducer(provider, ClickstreamHub.Name)));
        services.TryAddSingleton(provider => new HubProducer<BusinessEventsHub>(CreateProducer(provider, BusinessEventsHub.Name)));
        services.TryAddSingleton<IClickstreamPublisher, EventHubClickstreamPublisher>();
        services.TryAddSingleton<IBusinessEventPublisher, EventHubBusinessEventPublisher>();

        // Aggregates in Cosmos DB (analytics database). CosmosClient takes the application name from MessagingOptions.
        services.AddOptions<MessagingOptions>().Configure(options => options.ServiceName = MessagingTopology.Consumers.Analytics);
        services.AddShopFlowCosmos(configuration);
        services.TryAddSingleton<IMetricStore, CosmosMetricStore>();

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "cosmos-aggregates",
            provider => new CosmosContainerHealthCheck(provider.GetRequiredService<Database>().GetContainer(AnalyticsContainers.AggregatesName)),
            HealthStatus.Unhealthy,
            [HealthTags.Dependencies]));

        return services;
    }

    private static EventHubProducerClient CreateProducer(IServiceProvider provider, string hubName)
    {
        var options = provider.GetRequiredService<IOptions<EventHubsOptions>>().Value;
        return new EventHubProducerClient(
            options.FullyQualifiedNamespace,
            hubName,
            provider.GetRequiredService<TokenCredential>(),
            new EventHubProducerClientOptions { Identifier = $"analytics-processor-{Environment.MachineName}" });
    }
}
