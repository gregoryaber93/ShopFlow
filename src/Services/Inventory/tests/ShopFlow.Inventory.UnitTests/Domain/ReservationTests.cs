using ShopFlow.Inventory.Domain.Stock;

namespace ShopFlow.Inventory.UnitTests.Domain;

public sealed class ReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(60);

    private readonly List<Warehouse> _warehouses = [new("WAW-1", "Warsaw", 1), new("KRK-1", "Krakow", 2)];

    [Fact]
    public void Whole_order_is_reserved_in_the_highest_priority_warehouse_that_has_everything()
    {
        StockItem[] stock = [new("WAW-1", "A", 10, 0), new("WAW-1", "B", 10, 0), new("KRK-1", "A", 10, 0), new("KRK-1", "B", 10, 0)];

        var outcome = Allocate([new("A", 2), new("B", 1)], stock);

        outcome.Reservation!.WarehouseCode.ShouldBe("WAW-1");
        stock[0].Reserved.ShouldBe(2);
        stock[2].Reserved.ShouldBe(0);
    }

    [Fact]
    public void Warehouse_missing_one_line_is_skipped_even_if_it_has_priority()
    {
        StockItem[] stock = [new("WAW-1", "A", 10, 0), new("KRK-1", "A", 10, 0), new("KRK-1", "B", 10, 0)];

        Allocate([new("A", 1), new("B", 1)], stock).Reservation!.WarehouseCode.ShouldBe("KRK-1");
    }

    [Fact]
    public void Insufficient_stock_reports_the_best_single_warehouse_availability()
    {
        StockItem[] stock = [new("WAW-1", "A", 3, 0), new("KRK-1", "A", 4, 0)];

        var outcome = Allocate([new("A", 5)], stock);

        outcome.Succeeded.ShouldBeFalse();
        outcome.FailureCode.ShouldBe("insufficient_stock");
        outcome.Unavailable.ShouldHaveSingleItem().ShouldBe(new UnavailableLine("A", 5, 4));
        stock.ShouldAllBe(item => item.Reserved == 0);
    }

    [Fact]
    public void Sku_without_any_stock_row_is_unknown()
    {
        Allocate([new("NOPE", 1)], []).FailureCode.ShouldBe("unknown_sku");
    }

    [Fact]
    public void Duplicate_lines_are_merged_before_allocation()
    {
        StockItem[] stock = [new("WAW-1", "A", 3, 0)];

        Allocate([new("A", 2), new("a", 2)], stock).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Crossing_the_reorder_threshold_raises_stock_level_low_once()
    {
        StockItem[] stock = [new("WAW-1", "A", 12, reorderThreshold: 10)];

        var reservation = Allocate([new("A", 3)], stock).Reservation!;

        reservation.DomainEvents.OfType<StockLevelLowDomainEvent>().ShouldHaveSingleItem().Available.ShouldBe(9);
    }

    [Fact]
    public void Release_returns_the_quantities_and_a_second_release_changes_nothing()
    {
        StockItem[] stock = [new("WAW-1", "A", 10, 0)];
        var reservation = Allocate([new("A", 4)], stock).Reservation!;
        reservation.DequeueDomainEvents();

        reservation.Release(stock, Now).IsSuccess.ShouldBeTrue();
        reservation.Release(stock, Now).IsSuccess.ShouldBeTrue();

        stock[0].Reserved.ShouldBe(0);
        reservation.Status.ShouldBe(ReservationStatus.Released);
        reservation.DomainEvents.OfType<StockReleasedDomainEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Commit_removes_the_goods_from_stock_permanently()
    {
        StockItem[] stock = [new("WAW-1", "A", 10, 0)];
        var reservation = Allocate([new("A", 4)], stock).Reservation!;

        reservation.Commit(stock, Now).IsSuccess.ShouldBeTrue();

        stock[0].OnHand.ShouldBe(6);
        stock[0].Reserved.ShouldBe(0);
    }

    [Fact]
    public void Committing_an_expired_reservation_is_reported_as_possible_oversell()
    {
        StockItem[] stock = [new("WAW-1", "A", 10, 0)];
        var reservation = Allocate([new("A", 4)], stock).Reservation!;
        reservation.Expire(stock, Now + Ttl).ShouldBeTrue();

        reservation.Commit(stock, Now + Ttl).Error.Code.ShouldBe("reservation.not_active");
    }

    [Fact]
    public void Reservation_expires_only_after_its_time_to_live()
    {
        StockItem[] stock = [new("WAW-1", "A", 10, 0)];
        var reservation = Allocate([new("A", 4)], stock).Reservation!;

        reservation.Expire(stock, Now + Ttl - TimeSpan.FromSeconds(1)).ShouldBeFalse();
        reservation.Expire(stock, Now + Ttl).ShouldBeTrue();
        stock[0].Reserved.ShouldBe(0);
    }

    private AllocationOutcome Allocate(ReservationLine[] lines, IReadOnlyList<StockItem> stock) =>
        Reservation.Allocate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lines, _warehouses, stock, Now, Ttl);
}
