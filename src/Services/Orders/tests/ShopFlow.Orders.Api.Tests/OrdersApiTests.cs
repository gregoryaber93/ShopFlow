using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ShopFlow.Testing;
using ShopFlow.Resilience;

namespace ShopFlow.Orders.Api.Tests;

public sealed class OrdersApiTests(OrdersApiFactory factory) : IClassFixture<OrdersApiFactory>
{
    private static readonly object CourierDelivery = new
    {
        delivery = new
        {
            method = "courier",
            address = new { recipientName = "Jan Kowalski", line1 = "ul. Prosta 1", city = "Warszawa", postalCode = "00-001", countryCode = "PL" },
        },
        paymentMethodToken = "pm_tok_test",
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Order_without_idempotency_key_is_rejected()
    {
        var customer = await GivenCustomerWithCartAsync();

        var response = await SendAsync(HttpMethod.Post, "/v2/orders", customer, CourierDelivery);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Order_is_accepted_with_location_and_etag_and_announced_in_both_versions()
    {
        var customer = await GivenCustomerWithCartAsync();

        var response = await SendAsync(HttpMethod.Post, "/v2/orders", customer, CourierDelivery, idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.ETag.ShouldNotBeNull();
        var orderId = (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/v2/orders/{orderId}");
        PublishedFor(orderId).ShouldBe(["OrderSubmitted.v2", "OrderSubmitted.v1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Retry_with_the_same_key_returns_the_same_order_and_creates_nothing_new()
    {
        var customer = await GivenCustomerWithCartAsync();
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(HttpMethod.Post, "/v2/orders", customer, CourierDelivery, idempotencyKey: key);
        var retry = await SendAsync(HttpMethod.Post, "/v2/orders", customer, CourierDelivery, idempotencyKey: key);

        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        retry.Headers.GetValues(ShopFlowHttpHeaders.IdempotentReplayed).ShouldBe(["true"]);
        (await ReadJsonAsync(retry)).GetProperty("orderId").GetGuid().ShouldBe((await ReadJsonAsync(first)).GetProperty("orderId").GetGuid());
        factory.Store.Orders.Values.Count(order => order.State.CustomerId == customer).ShouldBe(1);
    }

    [Fact]
    public async Task V1_endpoint_announces_its_deprecation()
    {
        var customer = await GivenCustomerWithCartAsync();
        var body = new
        {
            shippingAddress = new { recipientName = "Jan", line1 = "ul. Prosta 1", city = "Warszawa", postalCode = "00-001", countryCode = "PL" },
            paymentMethodToken = "pm",
        };

        var response = await SendAsync(HttpMethod.Post, "/v1/orders", customer, body, idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.GetValues("Deprecation").ShouldHaveSingleItem();
        response.Headers.GetValues("Sunset").Single().ShouldContain("2027");
    }

    [Fact]
    public async Task Customer_cannot_read_another_customers_order()
    {
        var owner = await GivenCustomerWithCartAsync();
        var orderId = await SubmitAsync(owner);

        var response = await SendAsync(HttpMethod.Get, $"/v2/orders/{orderId}", Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Conditional_get_returns_304_while_the_order_is_unchanged()
    {
        var customer = await GivenCustomerWithCartAsync();
        var orderId = await SubmitAsync(customer);
        var first = await SendAsync(HttpMethod.Get, $"/v2/orders/{orderId}", customer);

        using var request = Request(HttpMethod.Get, $"/v2/orders/{orderId}", customer);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await _client.SendAsync(request, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Saga_confirmation_requires_if_match_and_rejects_a_stale_etag()
    {
        var customer = await GivenCustomerWithCartAsync();
        var orderId = await SubmitAsync(customer);

        var noEtag = await SendAsync(HttpMethod.Put, $"/internal/orders/{orderId}/confirmation", app: "Orders.Internal.Write", body: new { });
        noEtag.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);

        var stale = await SendAsync(HttpMethod.Put, $"/internal/orders/{orderId}/confirmation", app: "Orders.Internal.Write", body: new { }, ifMatch: "\"stale\"");
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Saga_confirms_the_order_with_the_current_etag()
    {
        var customer = await GivenCustomerWithCartAsync();
        var orderId = await SubmitAsync(customer);
        var current = await SendAsync(HttpMethod.Get, $"/internal/orders/{orderId}", app: "Orders.Internal.Write");

        var response = await SendAsync(
            HttpMethod.Put,
            $"/internal/orders/{orderId}/confirmation",
            app: "Orders.Internal.Write",
            body: new { trackingNumber = "TRK-1" },
            ifMatch: current.Headers.ETag!.Tag);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("status").GetString().ShouldBe("confirmed");
        response.Headers.ETag!.Tag.ShouldNotBe(current.Headers.ETag.Tag);
        PublishedFor(orderId).ShouldContain("OrderConfirmed.v1");
    }

    [Fact]
    public async Task Internal_endpoints_reject_customer_tokens_and_apps_without_the_role()
    {
        var customer = await GivenCustomerWithCartAsync();
        var orderId = await SubmitAsync(customer);

        // The customer token comes from another tenant, so the Workforce scheme does not authenticate it: 401.
        (await SendAsync(HttpMethod.Get, $"/internal/orders/{orderId}", customer)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A valid application token, but without the Orders.Internal.Write role: 403.
        (await SendAsync(HttpMethod.Get, $"/internal/orders/{orderId}", app: "Catalog.Prices.Read")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Catalog_outage_is_a_retryable_503()
    {
        var customer = await GivenCustomerWithCartAsync();
        factory.Catalog.Unavailable = true;

        try
        {
            var response = await SendAsync(HttpMethod.Post, "/v2/orders", customer, CourierDelivery, idempotencyKey: Guid.NewGuid().ToString());

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter.ShouldNotBeNull();
        }
        finally
        {
            factory.Catalog.Unavailable = false;
        }
    }

    [Fact]
    public async Task Invalid_delivery_is_a_validation_problem()
    {
        var customer = await GivenCustomerWithCartAsync();
        var body = new { delivery = new { method = "courier" }, paymentMethodToken = "pm" };

        var response = await SendAsync(HttpMethod.Post, "/v2/orders", customer, body, idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").EnumerateObject().ShouldNotBeEmpty();
    }

    private async Task<Guid> GivenCustomerWithCartAsync()
    {
        var customer = Guid.NewGuid();
        (await SendAsync(HttpMethod.Put, "/v1/cart/items/SKU-1", customer, new { quantity = 2 })).EnsureSuccessStatusCode();
        return customer;
    }

    private async Task<Guid> SubmitAsync(Guid customer)
    {
        var response = await SendAsync(HttpMethod.Post, "/v2/orders", customer, CourierDelivery, idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();
    }

    private string[] PublishedFor(Guid orderId) =>
    [
        .. factory.Store.Published
            .Where(message => message.Envelope.SessionId == orderId.ToString("D"))
            .Select(message => $"{message.Envelope.MessageType}.v{message.Envelope.ContractVersion}"),
    ];

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        Guid? customer = null,
        object? body = null,
        string? idempotencyKey = null,
        string? ifMatch = null,
        string? app = null)
    {
        using var request = Request(method, url, customer, app);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(ShopFlowHttpHeaders.IdempotencyKey, idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatch));
        }

        return await _client.SendAsync(request, Ct);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, Guid? customer = null, string? app = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (customer is { } id)
        {
            request.Headers.Add(TestAuthHandler.Header, $"customer:{id}");
        }
        else if (app is not null)
        {
            request.Headers.Add(TestAuthHandler.Header, $"app:{app}");
        }

        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
}
