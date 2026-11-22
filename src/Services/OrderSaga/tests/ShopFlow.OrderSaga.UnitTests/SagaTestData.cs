using ShopFlow.Contracts.Common;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.UnitTests;

internal static class SagaTestData
{
    public static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000aa");

    public static OrderSagaInput Input(bool pickupPoint = false) => new()
    {
        OrderId = OrderId,
        CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000bb"),
        OrderNumber = "SF-261121-SAGA0001",
        Lines = [new StockLineDto { Sku = "SKU-1", Quantity = 2 }],
        Total = new MoneyDto { Amount = 39.98m, Currency = "PLN" },
        Delivery = pickupPoint
            ? new SagaDelivery(true, null, "WAW-PP-01")
            : new SagaDelivery(false, new AddressDto { RecipientName = "Jan", Line1 = "ul. Prosta 1", City = "Warszawa", PostalCode = "00-001", CountryCode = "PL" }, null),
        PaymentMethodToken = "pm_tok",
        CorrelationId = "corr-saga",
        TriggeringMessageId = "0193a1b2-0000-7000-8000-0000000000cc",
        Timeouts = new SagaOptions().ToTimeouts(),
    };
}
