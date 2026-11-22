using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Contracts;
using ShopFlow.Messaging;

namespace ShopFlow.BuildingBlocks.Tests.Messaging;

/// <summary>DI container configured as in a service, with a registry of test contracts.</summary>
internal static class MessagingTestHost
{
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public static ServiceProvider Build(Action<MessagingBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        collection.AddSingleton(TestRegistry.Instance);

        var builder = collection.AddShopFlowMessaging("test-service")
            .Route<TestOrderPlaced>("test-topic")
            .Route<TestPing>("test-queue");

        configure?.Invoke(builder);
        services?.Invoke(collection);

        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static TestOrderPlaced SampleOrder(string note = "hello") => new()
    {
        OrderId = Guid.Parse("0193a1b2-0000-7000-8000-00000000000a"),
        CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-00000000000b"),
        Note = note,
    };

    public static MessageEnvelope Envelope(IServiceProvider provider, IMessage message, string correlationId = "corr-1") =>
        provider.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata(correlationId));
}
