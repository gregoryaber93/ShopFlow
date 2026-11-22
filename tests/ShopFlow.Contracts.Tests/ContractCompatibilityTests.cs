using System.Text.Json;
using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Orders;
using V1 = ShopFlow.Contracts.Orders.V1;
using V2 = ShopFlow.Contracts.Orders.V2;

namespace ShopFlow.Contracts.Tests;

/// <summary>Serialization behaviour that compatibility between deployments of different versions relies on.</summary>
public sealed class ContractCompatibilityTests
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-000000000002");

    [Fact]
    public void Unknown_fields_from_a_newer_publisher_are_ignored()
    {
        // The publisher added a "loyaltyPoints" field; a consumer with an older package must survive that.
        const string json = """
            {
              "orderId": "0193a1b2-0000-7000-8000-000000000001",
              "customerId": "0193a1b2-0000-7000-8000-000000000002",
              "orderNumber": "SF-2026-000001",
              "completedAt": "2026-11-14T10:00:00+00:00",
              "loyaltyPoints": 120
            }
            """;

        var message = JsonSerializer.Deserialize<V1.OrderCompleted>(json, ContractJson.Options);

        message.ShouldNotBeNull();
        message.OrderNumber.ShouldBe("SF-2026-000001");
    }

    [Fact]
    public void Missing_required_field_is_rejected_instead_of_defaulted()
    {
        // No orderNumber: an object with an empty number would break the customer e-mail three services further down the line.
        const string json = """
            {
              "orderId": "0193a1b2-0000-7000-8000-000000000001",
              "customerId": "0193a1b2-0000-7000-8000-000000000002",
              "completedAt": "2026-11-14T10:00:00+00:00"
            }
            """;

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<V1.OrderCompleted>(json, ContractJson.Options));
    }

    [Fact]
    public void Null_in_non_nullable_field_is_rejected()
    {
        const string json = """
            {
              "orderId": "0193a1b2-0000-7000-8000-000000000001",
              "customerId": "0193a1b2-0000-7000-8000-000000000002",
              "orderNumber": null,
              "completedAt": "2026-11-14T10:00:00+00:00"
            }
            """;

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<V1.OrderCompleted>(json, ContractJson.Options));
    }

    [Fact]
    public void Enums_travel_as_camel_case_strings()
    {
        var delivery = new V2.DeliveryDto { Method = V2.DeliveryMethod.PickupPoint, PickupPointId = "WAW-123" };

        var json = JsonSerializer.Serialize(delivery, ContractJson.Options);

        json.ShouldContain("\"method\":\"pickupPoint\"");
    }

    [Fact]
    public void Numeric_enum_values_are_rejected()
    {
        // A number instead of a name would mean that reordering enum values changes the meaning of the data.
        const string json = """{ "method": 1, "pickupPointId": "WAW-123" }""";

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<V2.DeliveryDto>(json, ContractJson.Options));
    }

    [Fact]
    public void Upcaster_turns_v1_order_into_courier_delivery_without_losing_data()
    {
        var address = new AddressDto
        {
            RecipientName = "Jan Kowalski",
            Line1 = "ul. Prosta 1",
            City = "Warszawa",
            PostalCode = "00-001",
            CountryCode = "PL",
        };

        var v1 = new V1.OrderSubmitted
        {
            OrderId = OrderId,
            CustomerId = CustomerId,
            OrderNumber = "SF-2026-000001",
            Lines = [new OrderLineDto { Sku = "SKU-1", ProductName = "Kubek", Quantity = 2, UnitPrice = new MoneyDto { Amount = 19.99m, Currency = "PLN" } }],
            Total = new MoneyDto { Amount = 39.98m, Currency = "PLN" },
            ShippingAddress = address,
            PaymentMethodToken = "pm_tok_123",
            SubmittedAt = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
        };

        var v2 = new OrderSubmittedV1ToV2Upcaster().Upcast(v1);

        v2.Delivery.Method.ShouldBe(V2.DeliveryMethod.Courier);
        v2.Delivery.Address.ShouldBe(address);
        v2.Lines.ShouldBe(v1.Lines);
        v2.Total.ShouldBe(v1.Total);
        v2.OrderId.ShouldBe(v1.OrderId);
    }

    [Fact]
    public void Both_versions_of_OrderSubmitted_are_registered_under_one_name()
    {
        var registry = MessageTypeRegistry.Default;

        registry.GetContractId<V1.OrderSubmitted>().ShouldBe(new MessageContractId("OrderSubmitted", 1));
        registry.GetContractId<V2.OrderSubmitted>().ShouldBe(new MessageContractId("OrderSubmitted", 2));
    }
}
