using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Application.Orders;
using ShopFlow.Orders.Domain.Orders;
using static ShopFlow.Orders.UnitTests.TestData;

namespace ShopFlow.Orders.UnitTests.Application;

public sealed class SubmitOrderHandlerTests : IDisposable
{
    private readonly OrdersTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Order_is_priced_from_the_catalog_saved_and_announced_in_both_contract_versions()
    {
        _host.Catalog.WithPrice("SKU-1", 19.99m).WithPrice("SKU-2", 5m);
        await _host.GivenCartAsync(("SKU-1", 2), ("SKU-2", 1));

        var result = await SubmitAsync(CourierCommand());

        var submitted = result.Value;
        submitted.Status.ShouldBe(OrderStatus.Submitted);
        var stored = _host.Store.Orders[submitted.OrderId].State;
        stored.Total.Amount.ShouldBe(44.98m);
        stored.Lines.Count.ShouldBe(2);

        // v2 always, v1 while the sunset period lasts (the flag is on by default in the fake).
        _host.Store.PublishedTypes().ShouldBe(["OrderSubmitted.v2", "OrderSubmitted.v1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Only_v2_is_published_once_the_v1_flag_is_switched_off()
    {
        _host.Features.PublishV1 = false;
        _host.Catalog.WithPrice("SKU-1", 10m);
        await _host.GivenCartAsync(("SKU-1", 1));

        await SubmitAsync(CourierCommand());

        _host.Store.PublishedTypes().ShouldBe(["OrderSubmitted.v2"]);
    }

    [Fact]
    public async Task Cart_is_cleared_after_the_order_is_saved()
    {
        _host.Catalog.WithPrice("SKU-1", 10m);
        await _host.GivenCartAsync(("SKU-1", 1));

        await SubmitAsync(CourierCommand());

        _host.Store.Carts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failure_to_clear_the_cart_does_not_fail_the_request()
    {
        // If the request ended with a 5xx, a retry with the same idempotency key would create a second order.
        // The order already exists, so the request must succeed.
        _host.Catalog.WithPrice("SKU-1", 10m);
        await _host.GivenCartAsync(("SKU-1", 1));
        _host.Store.FailCartDeletion = true;

        var result = await SubmitAsync(CourierCommand());

        result.IsSuccess.ShouldBeTrue();
        _host.Store.Orders.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Empty_cart_cannot_be_submitted()
    {
        (await SubmitAsync(CourierCommand())).Error.ShouldBe(SubmitOrderHandler.CartEmpty);
    }

    [Fact]
    public async Task Items_missing_from_the_catalog_reject_the_order_and_nothing_is_published()
    {
        _host.Catalog.WithPrice("SKU-1", 10m).WithPrice("SKU-2", 10m, available: false);
        await _host.GivenCartAsync(("SKU-1", 1), ("SKU-2", 1), ("SKU-GONE", 1));

        var result = await SubmitAsync(CourierCommand());

        result.Error.Code.ShouldBe("order.items_unavailable");
        result.Error.Message.ShouldContain("SKU-2");
        result.Error.Message.ShouldContain("SKU-GONE");
        _host.Store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pickup_point_is_refused_while_the_feature_is_off()
    {
        _host.Catalog.WithPrice("SKU-1", 10m);
        await _host.GivenCartAsync(("SKU-1", 1));

        var result = await SubmitAsync(new SubmitOrderCommand(CustomerId, new DeliveryInput(DeliveryMethod.PickupPoint, null, "WAW-01"), "pm"));

        result.Error.ShouldBe(SubmitOrderHandler.PickupPointUnavailable);
    }

    [Fact]
    public async Task Pickup_point_order_is_published_only_as_v2_even_during_v1_deprecation()
    {
        _host.Features.PickupPoint = true;
        _host.Catalog.WithPrice("SKU-1", 10m);
        await _host.GivenCartAsync(("SKU-1", 1));

        await SubmitAsync(new SubmitOrderCommand(CustomerId, new DeliveryInput(DeliveryMethod.PickupPoint, null, "WAW-01"), "pm"));

        _host.Store.PublishedTypes().ShouldBe(["OrderSubmitted.v2"]);
    }

    [Fact]
    public async Task Catalog_outage_surfaces_as_catalog_unavailable()
    {
        _host.Catalog.Unavailable = true;
        await _host.GivenCartAsync(("SKU-1", 1));

        await Should.ThrowAsync<CatalogUnavailableException>(() => SubmitAsync(CourierCommand()));
        _host.Store.Orders.ShouldBeEmpty();
    }

    private static SubmitOrderCommand CourierCommand() =>
        new(CustomerId, new DeliveryInput(DeliveryMethod.Courier, Address, null), "pm_tok_test");

    private Task<ShopFlow.SharedKernel.Results.Result<SubmittedOrder>> SubmitAsync(SubmitOrderCommand command) =>
        _host.InScopeAsync<SubmitOrderHandler, ShopFlow.SharedKernel.Results.Result<SubmittedOrder>>(
            handler => handler.HandleAsync(command, TestContext.Current.CancellationToken));
}
