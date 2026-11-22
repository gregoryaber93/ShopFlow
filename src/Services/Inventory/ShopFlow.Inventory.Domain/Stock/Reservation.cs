using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Inventory.Domain.Stock;

public enum ReservationStatus
{
    Active = 0,
    Committed = 1,
    Released = 2,
    Expired = 3,

    /// <summary>"Tombstone": the release arrived before the reservation. A later reservation for this order is rejected.</summary>
    Cancelled = 4,
}

public sealed record ReservationLine(string Sku, int Quantity);

public sealed record UnavailableLine(string Sku, int Requested, int Available);

public sealed record StockReservedDomainEvent(Guid ReservationId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record StockReleasedDomainEvent(Guid ReservationId, bool Expired, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ReservationCommittedDomainEvent(Guid ReservationId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Result of a reservation attempt: a reservation or a list of missing lines.</summary>
public sealed record AllocationOutcome(Reservation? Reservation, string? FailureCode, IReadOnlyList<UnavailableLine> Unavailable)
{
    public bool Succeeded => Reservation is not null;
}

/// <summary>
/// Stock reservation for one order. At most one reservation per order (unique index on OrderId).
/// </summary>
public sealed class Reservation : AggregateRoot<Guid>
{
    private readonly List<ReservationLine> _lines = [];

    private Reservation()
        : base(Guid.Empty)
    {
    }

    private Reservation(Guid id, Guid orderId, Guid customerId, DateTimeOffset now)
        : base(id)
    {
        OrderId = orderId;
        CustomerId = customerId;
        CreatedAt = now;
    }

    public Guid OrderId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string? WarehouseCode { get; private set; }

    public ReservationStatus Status { get; private set; }

    public IReadOnlyList<ReservationLine> Lines => _lines;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>
    /// Reserves the whole order in one warehouse (the first by priority that has all the lines).
    /// </summary>
    /// <remarks>
    /// WHY: a "one warehouse per order" policy. Splitting an order across warehouses means several parcels, several
    /// shipping fees and more steps in the saga. The StockReserved contract has a single WarehouseCode field, so this
    /// decision is already part of the public API. Changing the policy would require a new contract version.
    /// <para>
    /// WHY: the reservation lifetime is a safety net in case the saga disappears without compensating (e.g. a deployment
    /// error). It must be longer than the saga's longest path to confirmation (the sum of step timeouts); otherwise the
    /// stock goes back on sale before the order is confirmed and the shop oversells.
    /// </para>
    /// </remarks>
    public static AllocationOutcome Allocate(
        Guid reservationId,
        Guid orderId,
        Guid customerId,
        IReadOnlyList<ReservationLine> requested,
        IReadOnlyList<Warehouse> warehouses,
        IReadOnlyList<StockItem> stock,
        DateTimeOffset now,
        TimeSpan timeToLive)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(warehouses);
        ArgumentNullException.ThrowIfNull(stock);

        ReservationLine[] lines =
        [
            .. requested
                .GroupBy(line => line.Sku.ToUpperInvariant())
                .Select(group => new ReservationLine(group.Key, group.Sum(line => line.Quantity))),
        ];

        foreach (var warehouse in warehouses.Where(w => w.IsActive).OrderBy(w => w.Priority))
        {
            var items = lines.Select(line => (Line: line, Item: Find(stock, warehouse.Code, line.Sku))).ToArray();
            if (items.Any(pair => pair.Item is null || !pair.Item.CanReserve(pair.Line.Quantity)))
            {
                continue;
            }

            var reservation = new Reservation(reservationId, orderId, customerId, now)
            {
                WarehouseCode = warehouse.Code,
                Status = ReservationStatus.Active,
                ExpiresAt = now + timeToLive,
            };
            reservation._lines.AddRange(lines);

            foreach (var (line, item) in items)
            {
                if (item!.Reserve(line.Quantity, now) is { } lowStock)
                {
                    reservation.Raise(lowStock);
                }
            }

            reservation.Raise(new StockReservedDomainEvent(reservationId, now));
            return new AllocationOutcome(reservation, null, []);
        }

        var unknown = lines.Any(line => !stock.Any(item => item.Sku == line.Sku));
        UnavailableLine[] unavailable =
        [
            .. lines
                .Select(line => new UnavailableLine(line.Sku, line.Quantity, stock.Where(item => item.Sku == line.Sku).Select(item => item.Available).DefaultIfEmpty(0).Max()))
                .Where(line => line.Available < line.Requested),
        ];

        return new AllocationOutcome(null, unknown ? "unknown_sku" : "insufficient_stock", unavailable);
    }

    /// <summary>
    /// Tombstone reservation for an order whose release arrived before the reservation.
    /// </summary>
    /// <remarks>
    /// WHY: the inventory-commands queue has no sessions, so the commands of one order can be processed in parallel by
    /// different replicas. After a reservation timeout the saga sends ReleaseStock; if that release overtakes a late
    /// ReserveStock, without a tombstone the reservation would be created after the compensation and the stock would be
    /// blocked for an order that no longer exists (until the reservation expires).
    /// The tombstone occupies a slot in the unique index on OrderId, so the late reservation sees it and refuses.
    /// </remarks>
    public static Reservation Tombstone(Guid reservationId, Guid orderId, Guid customerId, DateTimeOffset now)
    {
        var tombstone = new Reservation(reservationId, orderId, customerId, now)
        {
            Status = ReservationStatus.Cancelled,
            ExpiresAt = now,
            ClosedAt = now,
        };

        tombstone.Raise(new StockReleasedDomainEvent(reservationId, Expired: false, now));
        return tombstone;
    }

    /// <summary>Compensation for the reservation step. State-tolerant: releasing a released reservation succeeds without changes.</summary>
    public Result Release(IReadOnlyList<StockItem> stock, DateTimeOffset now)
    {
        switch (Status)
        {
            case ReservationStatus.Active:
                foreach (var line in _lines)
                {
                    FindRequired(stock, line.Sku).Release(line.Quantity);
                }

                break;

            case ReservationStatus.Committed:
                // Compensation after commit: the goods go back to stock.
                foreach (var line in _lines)
                {
                    FindRequired(stock, line.Sku).Restock(line.Quantity);
                }

                break;

            default:
                return Result.Success();
        }

        Status = ReservationStatus.Released;
        ClosedAt = now;
        Raise(new StockReleasedDomainEvent(Id, Expired: false, now));
        return Result.Success();
    }

    /// <summary>The order was confirmed: the reservation becomes a permanent stock issue.</summary>
    public Result Commit(IReadOnlyList<StockItem> stock, DateTimeOffset now)
    {
        if (Status == ReservationStatus.Committed)
        {
            return Result.Success();
        }

        if (Status != ReservationStatus.Active)
        {
            return Error.Conflict(
                "reservation.not_active",
                $"Reservation {Id} for order {OrderId} is {Status}; a confirmed order has no reserved stock (possible oversell).");
        }

        foreach (var line in _lines)
        {
            FindRequired(stock, line.Sku).Commit(line.Quantity);
        }

        Status = ReservationStatus.Committed;
        ClosedAt = now;
        Raise(new ReservationCommittedDomainEvent(Id, now));
        return Result.Success();
    }

    /// <summary>Safety net: a reservation that nobody committed or released within its lifetime.</summary>
    public bool Expire(IReadOnlyList<StockItem> stock, DateTimeOffset now)
    {
        if (Status != ReservationStatus.Active || ExpiresAt > now)
        {
            return false;
        }

        foreach (var line in _lines)
        {
            FindRequired(stock, line.Sku).Release(line.Quantity);
        }

        Status = ReservationStatus.Expired;
        ClosedAt = now;
        Raise(new StockReleasedDomainEvent(Id, Expired: true, now));
        return true;
    }

    private static StockItem? Find(IReadOnlyList<StockItem> stock, string warehouseCode, string sku) =>
        stock.FirstOrDefault(item => item.WarehouseCode == warehouseCode && item.Sku == sku);

    private StockItem FindRequired(IReadOnlyList<StockItem> stock, string sku) =>
        Find(stock, WarehouseCode!, sku)
        ?? throw new DomainException("reservation.stock_missing", $"Stock row {WarehouseCode}/{sku} for reservation {Id} does not exist.");
}
