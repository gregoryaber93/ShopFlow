using System.Text.Json;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.Orders.Infrastructure.Cosmos;
using ShopFlow.Orders.Infrastructure.Support;
using ShopFlow.Persistence.Cosmos;
using static ShopFlow.Orders.UnitTests.TestData;

namespace ShopFlow.Orders.UnitTests.Infrastructure;

public sealed class OrderDocumentMapperTests
{
    [Fact]
    public void Document_uses_order_id_as_both_id_and_partition_key()
    {
        var order = ConfirmedOrder();

        var json = JsonSerializer.SerializeToElement(OrderDocumentMapper.ToDocument(order), CosmosJson.Options);

        json.GetProperty("id").GetString().ShouldBe(order.Id.ToString("D"));
        json.GetProperty("orderId").GetString().ShouldBe(order.Id.ToString("D"));
        json.GetProperty("docType").GetString().ShouldBe("order");
        json.GetProperty("schemaVersion").GetInt32().ShouldBe(OrderDocument.CurrentSchemaVersion);
        json.GetProperty("status").GetString().ShouldBe("confirmed");

        // Cosmos assigns the ETag; a document sent for writing must not contain it.
        json.TryGetProperty("_etag", out _).ShouldBeFalse();
    }

    [Fact]
    public void Order_survives_a_round_trip_through_json()
    {
        var order = ConfirmedOrder();
        var json = JsonSerializer.Serialize(OrderDocumentMapper.ToDocument(order), CosmosJson.Options);

        var restored = OrderDocumentMapper.ToDomain(JsonSerializer.Deserialize<OrderDocument>(json, CosmosJson.Options)!);

        restored.ToState().ShouldBeEquivalentTo(order.ToState() with { ConcurrencyToken = null });
    }

    [Fact]
    public void Schema_1_document_is_read_as_courier_delivery()
    {
        // A document saved before pickup points were introduced: a shippingAddress field instead of delivery.
        var legacy = $$"""
            {
              "id": "0193a1b2-0000-7000-8000-000000000099",
              "orderId": "0193a1b2-0000-7000-8000-000000000099",
              "docType": "order",
              "customerId": "{{CustomerId}}",
              "orderNumber": "SF-250101-OLD00001",
              "status": "completed",
              "lines": [ { "sku": "SKU-1", "productName": "Kubek", "quantity": 1, "unitPrice": { "amount": 10, "currency": "PLN" } } ],
              "total": { "amount": 10, "currency": "PLN" },
              "shippingAddress": { "recipientName": "Anna", "line1": "2 Long Street", "city": "Krakow", "postalCode": "30-001", "countryCode": "PL" },
              "paymentMethodToken": "pm",
              "submittedAt": "2025-01-01T10:00:00+00:00",
              "_etag": "\"legacy\"",
              "_ts": 1735725600
            }
            """;

        var order = OrderDocumentMapper.ToDomain(JsonSerializer.Deserialize<OrderDocument>(legacy, CosmosJson.Options)!);

        order.Delivery.Method.ShouldBe(DeliveryMethod.Courier);
        order.Delivery.Address!.City.ShouldBe("Krakow");
        order.ConcurrencyToken.ShouldBe("\"legacy\"");

        // The next save of this order will produce a schema 2 document.
        OrderDocumentMapper.ToDocument(order).SchemaVersion.ShouldBe(2);
    }

    [Fact]
    public void Document_from_a_newer_schema_is_refused_instead_of_misread()
    {
        var document = OrderDocumentMapper.ToDocument(ConfirmedOrder());
        var future = JsonSerializer.Deserialize<OrderDocument>(
            JsonSerializer.Serialize(document, CosmosJson.Options).Replace("\"schemaVersion\":2", "\"schemaVersion\":3", StringComparison.Ordinal),
            CosmosJson.Options)!;

        Should.Throw<InvalidOperationException>(() => OrderDocumentMapper.ToDomain(future)).Message.ShouldContain("newer");
    }

    [Fact]
    public void Order_numbers_are_readable_and_unique()
    {
        var generator = new OrderNumberGenerator();

        var numbers = Enumerable.Range(0, 1000).Select(_ => generator.Next(Now)).ToList();

        numbers.ShouldAllBe(number => System.Text.RegularExpressions.Regex.IsMatch(number, "^SF-261121-[0-9A-HJKMNP-TV-Z]{8}$"));
        numbers.Distinct().Count().ShouldBe(1000);
    }
}
