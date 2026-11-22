using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Orders.Application;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Application.Shipping;
using ShopFlow.Orders.Infrastructure;
using ShopFlow.Orders.Testing;

namespace ShopFlow.Orders.UnitTests.Application;

/// <summary>Application layer composed as in the service, with fakes instead of Cosmos DB and the catalog.</summary>
internal sealed class OrdersTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public OrdersTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Store);
        services.AddSingleton(Catalog);
        services.AddSingleton(Features);

        services.AddOrdersMessaging()
            .AddHandler<ShopFlow.Contracts.Shipping.V1.ShipmentDispatched, ShipmentDispatchedHandler>()
            .AddHandler<ShopFlow.Contracts.Shipping.V1.ShipmentDelivered, ShipmentDeliveredHandler>();
        services.AddOrdersApplication();

        services.AddScoped<IOrderRepository, InMemoryOrderRepository>();
        services.AddScoped<ICartRepository, InMemoryCartRepository>();
        services.AddSingleton<ICatalogPricing>(provider => provider.GetRequiredService<FakeCatalogPricing>());
        services.AddSingleton<IOrderFeatures>(provider => provider.GetRequiredService<FakeOrderFeatures>());
        services.AddSingleton<IOrderNumberGenerator, SequentialOrderNumbers>();

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public FakeTimeProvider Time { get; } = new(TestData.Now);

    public InMemoryOrderStore Store { get; } = new();

    public FakeCatalogPricing Catalog { get; } = new();

    public FakeOrderFeatures Features { get; } = new();

    public IServiceProvider Services => _provider;

    /// <summary>One scope = one operation (HTTP request or message), as in the service.</summary>
    public async Task<TResult> InScopeAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        using var scope = _provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task GivenCartAsync(params (string Sku, int Quantity)[] items)
    {
        using var scope = _provider.CreateScope();
        var carts = scope.ServiceProvider.GetRequiredService<ShopFlow.Orders.Application.Carts.CartService>();
        foreach (var (sku, quantity) in items)
        {
            (await carts.SetItemQuantityAsync(TestData.CustomerId, sku, quantity, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        }
    }

    public void Dispose() => _provider.Dispose();
}
