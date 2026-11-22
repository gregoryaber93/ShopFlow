using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Contracts;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Messaging.Routing;

namespace ShopFlow.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the messaging core. The broker adapter and the database adapter (outbox, inbox) are added separately.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="serviceName">Service name written to the Source header of outgoing messages.</param>
    public static MessagingBuilder AddShopFlowMessaging(this IServiceCollection services, string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.AddOptions<MessagingOptions>().Configure(options => options.ServiceName = serviceName);
        services.AddOptions<MessageRoutingOptions>();
        services.AddOptions<MessageHandlerOptions>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(MessageTypeRegistry.Default);
        services.TryAddSingleton<MessageEnvelopeFactory>();
        services.TryAddSingleton<IMessageRouter, MessageRouter>();
        services.TryAddSingleton<MessageDispatcher>();
        services.TryAddSingleton<MessageFailureClassifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFailureClassifier, DefaultFailureClassifier>());

        services.TryAddScoped<MessageContextAccessor>();
        services.TryAddScoped<IMessageContextAccessor>(provider => provider.GetRequiredService<MessageContextAccessor>());

        // One buffer instance under two interfaces: business logic sees IOutbox,
        // the database adapter sees IPendingOutbox.
        services.TryAddScoped<OutboxCollector>();
        services.TryAddScoped<IOutbox>(provider => provider.GetRequiredService<OutboxCollector>());
        services.TryAddScoped<IPendingOutbox>(provider => provider.GetRequiredService<OutboxCollector>());

        return new MessagingBuilder(services);
    }
}

/// <summary>Fluent configuration of the service's routes and handlers.</summary>
public sealed class MessagingBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Messages of this type will be sent to the given queue or topic.</summary>
    public MessagingBuilder Route<TMessage>(string destination)
        where TMessage : IMessage
    {
        Services.Configure<MessageRoutingOptions>(options => options.Map<TMessage>(destination));
        return this;
    }

    public MessagingBuilder AddHandler<TMessage, THandler>()
        where TMessage : class, IMessage
        where THandler : class, IMessageHandler<TMessage>
    {
        Services.TryAddScoped<THandler>();
        Services.Configure<MessageHandlerOptions>(options => options.Add<TMessage, THandler>());
        return this;
    }
}
