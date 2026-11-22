using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Testing;
using ShopFlow.Testing;
using ShopFlow.Web.Idempotency;

namespace ShopFlow.Orders.Api.Tests;

/// <summary>
/// The real orders-api Program.cs (routing, filters, policies, serialization) with fakes at the boundaries:
/// Cosmos DB, the catalog, flags and Entra ID.
/// </summary>
/// <remarks>
/// WHY: a component test, not an integration test. It checks everything that happens in the API process (HTTP
/// status, ETag/Idempotency-Key headers, error mapping, authorization) without Docker and without an Azure account.
/// Cosmos DB behaviour itself (TransactionalBatch, 412, 409) is covered by ShopFlow.Orders.IntegrationTests.
/// </remarks>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    public InMemoryOrderStore Store { get; } = new();

    public FakeCatalogPricing Catalog { get; } = new FakeCatalogPricing().WithPrice("SKU-1", 19.99m).WithPrice("SKU-2", 5m);

    public FakeOrderFeatures Features { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Cosmos:AccountEndpoint", "https://localhost:8081/");
        builder.UseSetting("Catalog:InventoryApiBaseUrl", "http://inventory.test/");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Store);
            services.Replace(ServiceDescriptor.Scoped<IOrderRepository, InMemoryOrderRepository>());
            services.Replace(ServiceDescriptor.Scoped<ICartRepository, InMemoryCartRepository>());
            services.RemoveAll<ICatalogPricing>();
            services.AddSingleton<ICatalogPricing>(Catalog);
            services.Replace(ServiceDescriptor.Singleton<IOrderFeatures>(Features));
            services.RemoveAll<IIdempotencyStore>();
            services.AddSingleton<IIdempotencyStore>(new InMemoryIdempotencyStore(TimeProvider.System));
            services.UseTestAuthentication();
        });
    }
}
