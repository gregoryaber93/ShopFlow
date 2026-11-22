using System.Net;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PactNet;
using PactNet.Matchers;
using ShopFlow.Messaging;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Application.Orders;
using ShopFlow.Orders.Application.Shipping;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.Orders.Infrastructure;
using ShopFlow.Orders.Infrastructure.Catalog;
using ShopFlow.Orders.Testing;
using ShopFlow.SharedKernel.Domain;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.Pact.Tests.Consumers;

/// <summary>
/// Orders as a consumer: what it expects from inventory-api (HTTP) and from Shipping events (messages).
/// </summary>
/// <remarks>
/// WHY: schema snapshots (ShopFlow.Contracts.Tests) protect the whole shape of a contract, regardless of who uses it. A pact
/// records which fields and values a specific consumer uses. The provider can therefore freely change fields nobody reads,
/// while a change to a field someone relies on stops their build before it reaches production. The pact runs against the
/// real consumer code: CatalogPricingClient and ShipmentDispatchedHandler here.
/// </remarks>
[Collection(PactCollection.Name)]
public sealed class OrdersConsumerPactTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Orders_api_reads_catalog_prices_from_inventory_api()
    {
        var pact = PactNet.Pact.V4("orders-api", "inventory-api", PactFiles.Config(output)).WithHttpInteractions();

        pact.UponReceiving("a request for the checkout price of one SKU")
            .Given("SKU-1 costs 19.99 PLN and is available")
            .WithRequest(HttpMethod.Get, "/internal/catalog/prices")
            .WithQuery("sku", "SKU-1")
            .WillRespond()
            .WithStatus(HttpStatusCode.OK)
            .WithHeader("Content-Type", Match.Regex("application/json; charset=utf-8", "^application/json.*$"))
            .WithJsonBody(new
            {
                // Only the fields CatalogPricingClient reads. The provider may add others without breaking the pact.
                items = Match.MinType(
                    new
                    {
                        sku = "SKU-1",
                        productName = Match.Type("Kubek"),
                        unitPrice = new { amount = Match.Decimal(19.99m), currency = Match.Regex("PLN", "^[A-Z]{3}$") },
                        isAvailable = Match.Type(true),
                    },
                    1),
            });

        await pact.VerifyAsync(async context =>
        {
            var client = new CatalogPricingClient(
                new HttpClient { BaseAddress = context.MockServerUri },
                Substitute.For<IDistributedCache>(),
                Options.Create(new CatalogOptions { InventoryApiBaseUrl = context.MockServerUri, Scope = "api://test/.default" }),
                NullLogger<CatalogPricingClient>.Instance);

            var prices = await client.GetPricesAsync(["SKU-1"], TestContext.Current.CancellationToken);

            prices["SKU-1"].UnitPrice.ShouldBe(new Money(19.99m, "PLN"));
            prices["SKU-1"].IsAvailable.ShouldBeTrue();
        });
    }

    [Fact]
    public async Task Orders_marks_the_order_shipped_when_shipping_reports_dispatch()
    {
        // A message pact in the V3 specification: the PactNet 5 verifier does not yet support the message transport of V4 pacts
        // on the provider side (the "builder error for url message://" error). The shape of the body and metadata is the same in
        // both versions.
        var pact = PactNet.Pact.V3("orders-processors", "shipping-processor", PactFiles.Config(output)).WithMessageInteractions();

        await pact.ExpectsToReceive("ShipmentDispatched for a confirmed order")
            .WithMetadata("subject", "ShipmentDispatched")
            .WithMetadata("contractVersion", "1")
            .WithJsonContent(new
            {
                orderId = Match.Regex(PactExamples.OrderId.ToString("D"), PactExamples.GuidPattern),
                trackingNumber = Match.Type("TRK-0001"),
                dispatchedAt = Match.Regex(PactExamples.DispatchedAt.ToString("O"), PactExamples.DateTimeOffsetPattern),

                // Fields required by the contract (deserialization), even though the Orders handler does not use them.
                customerId = Match.Regex(PactExamples.CustomerId.ToString("D"), PactExamples.GuidPattern),
                shipmentId = Match.Regex(PactExamples.ShipmentId.ToString("D"), PactExamples.GuidPattern),
                carrierCode = Match.Type("DPD"),
            })
            .VerifyAsync<ShippingV1.ShipmentDispatched>(async message =>
            {
                var store = new InMemoryOrderStore();
                store.Orders[PactExamples.OrderId] = (ConfirmedOrder().ToState(), store.NextETag());

                var services = new ServiceCollection();
                services.AddOrdersMessaging();
                services.AddSingleton(store);
                services.AddSingleton<IOrderFeatures, FakeOrderFeatures>();
                services.AddScoped<IOrderRepository, InMemoryOrderRepository>();
                services.AddScoped<OrderEventPublisher>();
                services.AddScoped<ShipmentDispatchedHandler>();
                await using var provider = services.BuildServiceProvider();
                await using var scope = provider.CreateAsyncScope();

                await scope.ServiceProvider.GetRequiredService<ShipmentDispatchedHandler>().HandleAsync(message, Context(), TestContext.Current.CancellationToken);

                store.Orders[PactExamples.OrderId].State.Status.ShouldBe(OrderStatus.Shipped);
            });
    }

    private static Order ConfirmedOrder()
    {
        var now = PactExamples.DispatchedAt.AddHours(-1);
        var order = Order.Submit(
            PactExamples.OrderId,
            PactExamples.CustomerId,
            "SF-PACT-0001",
            [new OrderLine("SKU-1", "Kubek", 1, new Money(19.99m, "PLN"))],
            Delivery.Courier(new ShippingAddress("Jan Kowalski", "ul. Prosta 1", null, "Warszawa", "00-001", "PL", null)),
            "pm_tok_test",
            now).Value;
        order.Confirm("TRK-0001", now);
        order.DequeueDomainEvents();
        return order;
    }

    private static MessageContext Context() => new()
    {
        MessageId = Guid.CreateVersion7(),
        MessageType = "ShipmentDispatched",
        ContractVersion = 1,
        CorrelationId = "pact",
        Source = "shipping",
        OccurredAt = PactExamples.DispatchedAt,
        Consumer = "orders-processors",
        DeliveryCount = 1,
        EnqueuedAt = PactExamples.DispatchedAt,
    };
}
