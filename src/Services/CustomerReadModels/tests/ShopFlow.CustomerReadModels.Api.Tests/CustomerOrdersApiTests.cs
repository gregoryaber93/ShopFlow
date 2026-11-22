using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.CustomerReadModels.Infrastructure;
using ShopFlow.CustomerReadModels.Testing;
using ShopFlow.Testing;

namespace ShopFlow.CustomerReadModels.Api.Tests;

public sealed class ReadModelsApiFactory : WebApplicationFactory<Program>
{
    public static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000f1");

    public static readonly Guid OrderId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-3));

    public static readonly Guid OrderWithoutInvoiceId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-1));

    public InMemoryCustomerOrderStore Store { get; } = new();

    public InMemoryOrderDetailsCache Cache { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Store.Seed(View(OrderId, "SF-2026-000010", DateTimeOffset.UtcNow.AddDays(-3), withInvoice: true));
        Store.Seed(View(OrderWithoutInvoiceId, "SF-2026-000011", DateTimeOffset.UtcNow.AddDays(-1), withInvoice: false));
        Store.Seed(CustomerOrderView.Placeholder(CustomerId, Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-2))));

        builder.UseEnvironment("Testing");
        builder.UseSetting("Cosmos:accountEndpoint", "https://cosmos-test.documents.azure.com:443/");
        builder.UseSetting("Cosmos:DatabaseName", "readmodels");

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICustomerOrderStore>(Store));
            services.Replace(ServiceDescriptor.Singleton<IOrderDetailsCache>(Cache));
            services.Replace(ServiceDescriptor.Singleton<IInvoiceDownloadLinks>(new FakeInvoiceLinks()));
            services.UseTestAuthentication();
        });
    }

    private static CustomerOrderView View(Guid orderId, string number, DateTimeOffset submittedAt, bool withInvoice)
    {
        var view = CustomerOrderView.Placeholder(CustomerId, orderId);
        view.OrderNumber = number;
        view.SubmittedAt = submittedAt;
        view.Total = new MoneyView(99.99m, "PLN");
        view.Lines = [new OrderLineView("SKU-1", "Kubek", 1, new MoneyView(99.99m, "PLN"))];
        view.ConfirmedAt = submittedAt.AddMinutes(1);
        view.Invoice = withInvoice ? new InvoiceView("FV-2026-11-000010", "2026/11/fv-10.pdf", submittedAt.AddMinutes(2)) : null;
        view.Status = CustomerOrderStatus.Derive(view);
        return view;
    }

    private sealed class FakeInvoiceLinks : IInvoiceDownloadLinks
    {
        public Task<InvoiceLink> CreateAsync(string blobPath, CancellationToken cancellationToken) =>
            Task.FromResult(new InvoiceLink(new Uri($"https://files.shopflow.example/invoices/{blobPath}?sp=r&sig=test"), DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}

public sealed class CustomerOrdersApiTests(ReadModelsApiFactory factory) : IClassFixture<ReadModelsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Customer_reads_own_order_with_a_private_etag_and_gets_304_when_nothing_changed()
    {
        using var first = Get($"/v1/me/orders/{ReadModelsApiFactory.OrderId}", ReadModelsApiFactory.CustomerId);
        var response = await _client.SendAsync(first, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        var etag = response.Headers.ETag!.Tag;
        var body = await response.Content.ReadFromJsonAsync<OrderDetailsResponse>(Json, Ct);
        body!.OrderNumber.ShouldBe("SF-2026-000010");
        body.Status.ShouldBe(CustomerOrderStatus.Confirmed);
        body.Invoice!.DownloadPath.ShouldBe($"/v1/me/orders/{ReadModelsApiFactory.OrderId:D}/invoice");

        using var second = Get($"/v1/me/orders/{ReadModelsApiFactory.OrderId}", ReadModelsApiFactory.CustomerId);
        second.Headers.TryAddWithoutValidation("If-None-Match", etag);

        (await _client.SendAsync(second, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Another_customer_cannot_see_the_order_even_when_it_is_cached()
    {
        using var owner = Get($"/v1/me/orders/{ReadModelsApiFactory.OrderId}", ReadModelsApiFactory.CustomerId);
        (await _client.SendAsync(owner, Ct)).EnsureSuccessStatusCode();

        using var stranger = Get($"/v1/me/orders/{ReadModelsApiFactory.OrderId}", Guid.NewGuid());

        (await _client.SendAsync(stranger, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Order_placed_moments_ago_returns_retry_after_instead_of_plain_not_found()
    {
        using var request = Get($"/v1/me/orders/{Guid.CreateVersion7()}", ReadModelsApiFactory.CustomerId);

        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(2));
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(CustomerOrderEndpoints.ProjectionPendingProblem);
    }

    [Fact]
    public async Task List_shows_only_complete_orders_newest_first()
    {
        using var request = Get("/v1/me/orders?pageSize=1", ReadModelsApiFactory.CustomerId);

        var page = await (await _client.SendAsync(request, Ct)).Content.ReadFromJsonAsync<OrderListResponse>(Json, Ct);

        page!.Items.ShouldHaveSingleItem().OrderNumber.ShouldBe("SF-2026-000011");
        page.NextPageToken.ShouldNotBeNull();

        using var next = Get($"/v1/me/orders?pageSize=1&pageToken={page.NextPageToken}", ReadModelsApiFactory.CustomerId);
        var last = await (await _client.SendAsync(next, Ct)).Content.ReadFromJsonAsync<OrderListResponse>(Json, Ct);

        last!.Items.ShouldHaveSingleItem().OrderNumber.ShouldBe("SF-2026-000010");
        last.NextPageToken.ShouldBeNull();
    }

    [Fact]
    public async Task Invoice_link_is_issued_for_the_owner_and_never_cached()
    {
        using var request = Get($"/v1/me/orders/{ReadModelsApiFactory.OrderId}/invoice", ReadModelsApiFactory.CustomerId);

        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var link = await response.Content.ReadFromJsonAsync<InvoiceLinkResponse>(Json, Ct);
        link!.Url.Host.ShouldBe("files.shopflow.example");

        using var noInvoice = Get($"/v1/me/orders/{ReadModelsApiFactory.OrderWithoutInvoiceId}/invoice", ReadModelsApiFactory.CustomerId);
        (await _client.SendAsync(noInvoice, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_caller_is_rejected()
    {
        (await _client.GetAsync("/v1/me/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpRequestMessage Get(string url, Guid customerId) => new HttpRequestMessage(HttpMethod.Get, url).AsCustomer(customerId);
}
