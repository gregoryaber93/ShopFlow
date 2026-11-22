using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.UnitTests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    public static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000c1");

    public static readonly ShippingAddress Address = new("Jan Kowalski", "ul. Prosta 1", null, "Warszawa", "00-001", "PL", "+48500100200");

    public static OrderLine Line(string sku = "SKU-1", int quantity = 1, decimal price = 10m, string currency = "PLN") =>
        new(sku, $"Product {sku}", quantity, new Money(price, currency));

    public static Order SubmittedOrder(params OrderLine[] lines)
    {
        var order = Order.Submit(
            Guid.CreateVersion7(Now),
            CustomerId,
            "SF-261121-TEST0001",
            lines.Length == 0 ? [Line()] : lines,
            Delivery.Courier(Address),
            "pm_tok_test",
            Now).Value;

        order.DequeueDomainEvents();
        order.SetConcurrencyToken("\"etag-1\"");
        return order;
    }

    public static Order ConfirmedOrder()
    {
        var order = SubmittedOrder();
        order.Confirm("TRK-1", Now).IsSuccess.ShouldBeTrue();
        order.DequeueDomainEvents();
        return order;
    }
}
