using System.Text.Json;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Inventory.Application.Reservations;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.Messaging.Failures;

namespace ShopFlow.Inventory.UnitTests.Application;

public sealed class ReservationHandlerTests : IDisposable
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000d1");
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000d2");

    private readonly InventoryTestHost _host = new();

    public ReservationHandlerTests() => _host.State.WithStock("WAW-1", "SKU-1", onHand: 10).WithStock("WAW-1", "SKU-2", onHand: 1);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Reservation_is_made_and_announced()
    {
        await _host.DispatchAsync(Reserve(("SKU-1", 3)));

        _host.State.StockOf("WAW-1", "SKU-1").Reserved.ShouldBe(3);
        var reply = SinglePublished<StockReserved>();
        reply.WarehouseCode.ShouldBe("WAW-1");
        reply.ExpiresAt.ShouldBe(_host.Time.GetUtcNow().AddHours(1));
    }

    [Fact]
    public async Task Insufficient_stock_is_answered_with_details_and_nothing_is_reserved()
    {
        await _host.DispatchAsync(Reserve(("SKU-2", 5)));

        _host.State.Reservations.ShouldBeEmpty();
        var reply = SinglePublished<StockReservationFailed>();
        reply.ReasonCode.ShouldBe("insufficient_stock");
        reply.UnavailableLines.ShouldHaveSingleItem().Available.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_reserve_command_for_the_same_order_repeats_the_answer_without_reserving_twice()
    {
        await _host.DispatchAsync(Reserve(("SKU-1", 3)));
        await _host.DispatchAsync(Reserve(("SKU-1", 3)), Guid.NewGuid());

        _host.State.StockOf("WAW-1", "SKU-1").Reserved.ShouldBe(3);
        _host.State.PublishedTypes().ShouldBe(["StockReserved", "StockReserved"]);
    }

    [Fact]
    public async Task Release_that_overtakes_reserve_leaves_a_tombstone_that_blocks_the_late_reservation()
    {
        // After a timeout the saga sent ReleaseStock, which was processed before the late ReserveStock.
        await _host.DispatchAsync(Release());
        await _host.DispatchAsync(Reserve(("SKU-1", 3)));

        _host.State.StockOf("WAW-1", "SKU-1").Reserved.ShouldBe(0);
        _host.State.Reservations.ShouldHaveSingleItem().Status.ShouldBe(ReservationStatus.Cancelled);
        _host.State.PublishedTypes().ShouldBe(["StockReleased", "StockReservationFailed"]);
        Published<StockReservationFailed>().Single().ReasonCode.ShouldBe("reservation_cancelled");
    }

    [Fact]
    public async Task Every_release_is_answered_but_stock_is_returned_once()
    {
        await _host.DispatchAsync(Reserve(("SKU-1", 3)));
        _host.State.ClearPublished();

        await _host.DispatchAsync(Release());
        await _host.DispatchAsync(Release(), Guid.NewGuid());

        _host.State.StockOf("WAW-1", "SKU-1").Reserved.ShouldBe(0);
        _host.State.PublishedTypes().ShouldBe(["StockReleased", "StockReleased"]);
    }

    [Fact]
    public async Task Order_confirmation_turns_the_reservation_into_a_permanent_issue()
    {
        await _host.DispatchAsync(Reserve(("SKU-1", 3)));

        await _host.DispatchAsync(Confirmed());

        var stock = _host.State.StockOf("WAW-1", "SKU-1");
        stock.OnHand.ShouldBe(7);
        stock.Reserved.ShouldBe(0);
    }

    [Fact]
    public async Task Confirmation_after_the_reservation_expired_goes_to_the_dlq()
    {
        await _host.DispatchAsync(Reserve(("SKU-1", 3)));
        _host.Time.Advance(TimeSpan.FromMinutes(61));
        (await _host.InScopeAsync<ExpireReservationHandler, bool>(handler =>
            handler.ExpireAsync(_host.State.Reservations.Single().Id, CancellationToken.None))).ShouldBeTrue();

        var exception = await Should.ThrowAsync<PermanentMessageException>(() => _host.DispatchAsync(Confirmed()));

        exception.Message.ShouldContain("oversell");
    }

    private static ReserveStock Reserve(params (string Sku, int Quantity)[] lines) => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        Lines = [.. lines.Select(line => new StockLineDto { Sku = line.Sku, Quantity = line.Quantity })],
    };

    private static ReleaseStock Release() => new() { OrderId = OrderId, CustomerId = CustomerId, Reason = "payment_declined" };

    private static OrderConfirmed Confirmed() => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        OrderNumber = "SF-1",
        Total = new MoneyDto { Amount = 1m, Currency = "PLN" },
        ConfirmedAt = DateTimeOffset.UtcNow,
    };

    private T SinglePublished<T>() => Published<T>().ShouldHaveSingleItem();

    private IReadOnlyList<T> Published<T>()
    {
        var contract = MessageTypeRegistry.Default.GetContractId(typeof(T));
        return
        [
            .. _host.State.Published
                .Where(message => message.Envelope.MessageType == contract.Name)
                .Select(message => JsonSerializer.Deserialize<T>(message.Envelope.Payload.Span, ContractJson.Options)!),
        ];
    }
}
