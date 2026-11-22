using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Testing;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.Testing;

namespace ShopFlow.Inventory.Api.Tests;

public sealed class InventoryApiFactory : WebApplicationFactory<Program>
{
    public InMemoryInventoryState State { get; } = new InMemoryInventoryState()
        .WithWarehouse("WAW-1", 1)
        .WithProduct("SKU-1", 19.99m)
        .WithStock("WAW-1", "SKU-1", onHand: 5);

    public FakeCatalogReader Catalog { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Catalog.Products.Add(new ProductView("SKU-1", "Kubek", null, 1, new Money(19.99m, "PLN"), true, 5, "\"v1\""));
        Catalog.Prices.Add(new PriceView("SKU-1", "Kubek", new Money(19.99m, "PLN"), true));

        builder.UseEnvironment("Testing");
        builder.UseSetting("Sql:ConnectionString", "Server=tcp:test,1433;Database=sqldb-inventory");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(State);
            services.AddScoped<InMemoryInventoryStore>();
            services.Replace(ServiceDescriptor.Scoped<IStockRepository>(provider => provider.GetRequiredService<InMemoryInventoryStore>()));
            services.Replace(ServiceDescriptor.Scoped<IProductRepository>(provider => provider.GetRequiredService<InMemoryInventoryStore>()));
            services.Replace(ServiceDescriptor.Scoped<IInventoryUnitOfWork>(provider => provider.GetRequiredService<InMemoryInventoryStore>()));
            services.Replace(ServiceDescriptor.Singleton<ICatalogReader>(Catalog));
            services.UseTestAuthentication();
        });
    }
}

public sealed class InventoryApiTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Prices_response_is_readable_with_the_shape_orders_api_expects()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/catalog/prices?sku=SKU-1&sku=SKU-UNKNOWN").AsApp("Catalog.Prices.Read");

        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A copy of the types CatalogPricingClient in Orders uses to read this response. If Inventory renamed a field, this
        // test (and the Pact contract) would catch it before deployment.
        var body = await response.Content.ReadFromJsonAsync<OrdersView.PricesResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web), Ct);
        var item = body!.Items.ShouldHaveSingleItem();
        item.Sku.ShouldBe("SKU-1");
        item.ProductName.ShouldBe("Kubek");
        item.UnitPrice.ShouldBe(new OrdersView.Money(19.99m, "PLN"));
        item.IsAvailable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("customer", HttpStatusCode.Unauthorized)]
    [InlineData("app-without-role", HttpStatusCode.Forbidden)]
    public async Task Prices_are_only_for_services_with_the_role(string? caller, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/catalog/prices?sku=SKU-1");
        _ = caller switch
        {
            "customer" => request.AsCustomer(Guid.NewGuid()),
            "app-without-role" => request.AsApp("Stock.Receipts.Write"),
            _ => request,
        };

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Public_product_page_is_anonymous_and_cacheable()
    {
        var response = await _client.GetAsync("/v1/catalog/products/SKU-1", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.ETag!.Tag.ShouldBe("\"v1\"");
        (await _client.GetAsync("/v1/catalog/products/NOPE", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Price_change_requires_a_catalog_manager_and_the_current_etag()
    {
        (await ChangePriceAsync(etag: null, request => request.AsUser("Catalog.Manager"))).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await ChangePriceAsync("\"v0\"", request => request.AsUser("Catalog.Manager"))).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        // An application token with the same role is not an employee: no scp.
        (await ChangePriceAsync("\"v1\"", request => request.AsApp("Catalog.Manager"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var accepted = await ChangePriceAsync("\"v1\"", request => request.AsUser("Catalog.Manager"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        accepted.Headers.ETag!.Tag.ShouldBe("\"v2\"");
    }

    [Fact]
    public async Task Repeated_stock_receipt_is_accepted_once()
    {
        var receipt = new { receiptId = Guid.NewGuid(), warehouseCode = "WAW-1", sku = "SKU-1", quantity = 3 };

        var first = await SendAsync(HttpMethod.Post, "/v1/admin/stock/receipts", receipt, request => request.AsApp("Stock.Receipts.Write"));
        var second = await SendAsync(HttpMethod.Post, "/v1/admin/stock/receipts", receipt, request => request.AsApp("Stock.Receipts.Write"));

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private Task<HttpResponseMessage> ChangePriceAsync(string? etag, Func<HttpRequestMessage, HttpRequestMessage> caller) =>
        SendAsync(HttpMethod.Put, "/v1/admin/products/SKU-1/price", new { amount = 24.99m, currency = "PLN" }, request =>
        {
            if (etag is not null)
            {
                request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
            }

            return caller(request);
        });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object body, Func<HttpRequestMessage, HttpRequestMessage> configure)
    {
        using var request = configure(new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) });
        return await _client.SendAsync(request, Ct);
    }

    private static class OrdersView
    {
        public sealed record PricesResponse(IReadOnlyList<PriceItem> Items);

        public sealed record PriceItem(string Sku, string ProductName, Money UnitPrice, bool IsAvailable);

        public sealed record Money(decimal Amount, string Currency);
    }
}
