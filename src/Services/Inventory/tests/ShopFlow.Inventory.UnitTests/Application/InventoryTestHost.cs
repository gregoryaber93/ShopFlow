using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Inventory.Application;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Application.Reservations;
using ShopFlow.Inventory.Infrastructure;
using ShopFlow.Inventory.Testing;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;

namespace ShopFlow.Inventory.UnitTests.Application;

/// <summary>Inventory application layer composed as in inventory-worker, with an in-memory store.</summary>
internal sealed class InventoryTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public InventoryTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(State);

        services.AddInventoryMessaging()
            .AddHandler<ReserveStock, ReserveStockHandler>()
            .AddHandler<ReleaseStock, ReleaseStockHandler>()
            .AddHandler<OrderConfirmed, OrderConfirmedHandler>();
        services.AddInventoryApplication(new ConfigurationBuilder().Build());

        services.AddScoped<InMemoryInventoryStore>();
        services.AddScoped<IStockRepository>(provider => provider.GetRequiredService<InMemoryInventoryStore>());
        services.AddScoped<IProductRepository>(provider => provider.GetRequiredService<InMemoryInventoryStore>());
        services.AddScoped<IInventoryUnitOfWork>(provider => provider.GetRequiredService<InMemoryInventoryStore>());
        services.AddSingleton<ICatalogReader>(Catalog);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

    public InMemoryInventoryState State { get; } = new InMemoryInventoryState()
        .WithWarehouse("WAW-1", 1)
        .WithWarehouse("KRK-1", 2);

    public FakeCatalogReader Catalog { get; } = new();

    public IServiceProvider Services => _provider;

    /// <summary>A message through the real MessageDispatcher (scope, context, inbox) as in the worker.</summary>
    public Task<DispatchOutcome> DispatchAsync(IMessage message, Guid? messageId = null)
    {
        var envelope = _provider.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata("corr"), messageId);
        return _provider.GetRequiredService<MessageDispatcher>().DispatchAsync(
            envelope,
            new ConsumerEndpoint(MessagingTopology.Consumers.InventoryWorker),
            new DeliveryInfo(1, Time.GetUtcNow()),
            CancellationToken.None);
    }

    public async Task<TResult> InScopeAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        using var scope = _provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public void Dispose() => _provider.Dispose();
}
