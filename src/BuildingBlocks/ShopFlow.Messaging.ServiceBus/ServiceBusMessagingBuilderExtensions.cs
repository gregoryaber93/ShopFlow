using Azure.Core;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Outbox;

namespace ShopFlow.Messaging.ServiceBus;

public static class ServiceBusMessagingBuilderExtensions
{
    /// <summary>
    /// Plugs in Azure Service Bus as the broker. Requires a registered <see cref="TokenCredential"/>
    /// (<c>ShopFlow.Azure</c> registers it).
    /// </summary>
    public static MessagingBuilder UseAzureServiceBus(this MessagingBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var services = builder.Services;

        services.AddOptions<ServiceBusOptions>()
            .Bind(configuration.GetSection(ServiceBusOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ServiceBusProcessingOptions>()
            .Bind(configuration.GetSection(ServiceBusProcessingOptions.SectionName));

        services.TryAddSingleton(CreateClient);
        services.TryAddSingleton<IMessagePublisher, ServiceBusMessagePublisher>();
        services.TryAddSingleton<ServiceBusMessageProcessor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFailureClassifier, ServiceBusFailureClassifier>());

        return builder;
    }

    /// <summary>Adds a queue or subscription consumer running as a background service.</summary>
    public static MessagingBuilder AddServiceBusConsumer(
        this MessagingBuilder builder,
        Action<ServiceBusConsumerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var consumerOptions = new ServiceBusConsumerOptions();
        configure(consumerOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerOptions.EntityPath);

        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new ServiceBusQueueConsumer(
            provider.GetRequiredService<ServiceBusClient>(),
            provider.GetRequiredService<ServiceBusMessageProcessor>(),
            consumerOptions,
            provider.GetRequiredService<ILogger<ServiceBusQueueConsumer>>()));

        return builder;
    }

    private static ServiceBusClient CreateClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
        var messaging = provider.GetRequiredService<IOptions<MessagingOptions>>().Value;

        // AZURE: the (namespace, TokenCredential) constructor means Entra ID authentication.
        // There is no SAS key here at all. Bicep sets disableLocalAuth = true on the namespace,
        // so a connection string with a key would be rejected by the broker anyway.
        return new ServiceBusClient(
            options.FullyQualifiedNamespace,
            provider.GetRequiredService<TokenCredential>(),
            new ServiceBusClientOptions
            {
                TransportType = options.UseWebSockets
                    ? ServiceBusTransportType.AmqpWebSockets
                    : ServiceBusTransportType.AmqpTcp,
                Identifier = $"{messaging.ServiceName}-{Environment.MachineName}",
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = options.Retry.MaxRetries,
                    Delay = options.Retry.Delay,
                    MaxDelay = options.Retry.MaxDelay,
                    TryTimeout = options.Retry.TryTimeout,
                },
            });
    }
}
