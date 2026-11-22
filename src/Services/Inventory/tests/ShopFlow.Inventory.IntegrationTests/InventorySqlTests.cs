using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Domain.Catalog;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.Inventory.Infrastructure.Persistence;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.SqlServer.Messaging;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Inventory.IntegrationTests;

public sealed class InventorySqlTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string SkipReason = "Integration tests need Docker; set SHOPFLOW_RUN_INTEGRATION_TESTS=1 to run them.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_create_the_schema_with_reference_data()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, SkipReason);
        using var provider = fixture.BuildServices();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        (await db.Warehouses.CountAsync(Ct)).ShouldBe(2);
        (await db.Countries.CountAsync(Ct)).ShouldBe(5);
        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_constraint_rejects_reserving_more_than_on_hand_even_outside_the_domain()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, SkipReason);
        using var provider = fixture.BuildServices();
        var sku = await GivenProductWithStockAsync(provider, onHand: 2);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var exception = await Should.ThrowAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE [inventory].[StockItems] SET [Reserved] = [OnHand] + 1 WHERE [Sku] = {sku}", Ct));

        exception.Number.ShouldBe(547);
    }

    [Fact]
    public async Task Two_reservations_of_the_last_unit_cannot_both_succeed()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, SkipReason);
        using var provider = fixture.BuildServices();
        var sku = await GivenProductWithStockAsync(provider, onHand: 1);

        // Two processes (two worker replicas) read the same state before either of them writes.
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstAttempt = await ReserveAsync(first, sku);
        var secondAttempt = await ReserveAsync(second, sku);

        await firstAttempt();

        // rowversion changed after the first write: the second process's UPDATE finds no row.
        await Should.ThrowAsync<ConcurrencyConflictException>(secondAttempt);
    }

    [Fact]
    public async Task Concurrent_outbox_relays_claim_disjoint_batches()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, SkipReason);
        using var provider = fixture.BuildServices();
        await GivenOutboxMessagesAsync(provider, count: 10);

        var published = new List<Guid>();
        fixture.Publisher
            .PublishAsync(Arg.Do<IReadOnlyCollection<OutboundMessage>>(batch => { lock (published) { published.AddRange(batch.Select(message => message.Envelope.MessageId)); } }), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        await Task.WhenAll(
            scopeA.ServiceProvider.GetRequiredService<SqlOutboxRelay<InventoryDbContext>>().PublishPendingAsync(Ct),
            scopeB.ServiceProvider.GetRequiredService<SqlOutboxRelay<InventoryDbContext>>().PublishPendingAsync(Ct));

        // READPAST: each replica took different rows, no message was sent twice.
        published.Count.ShouldBe(published.Distinct().Count());
        published.Count.ShouldBe(10);
    }

    private static async Task<string> GivenProductWithStockAsync(IServiceProvider provider, int onHand)
    {
        var sku = $"IT-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.Products.Add(Product.Create(sku, "Integration product", null, 1, new Money(10m, "PLN"), DateTimeOffset.UtcNow));
        db.StockItems.Add(new StockItem("WAW-1", sku, onHand, 0));
        await db.SaveChangesAsync(Ct);
        return sku;
    }

    private static async Task<Func<Task>> ReserveAsync(IServiceScope scope, string sku)
    {
        var stock = scope.ServiceProvider.GetRequiredService<IStockRepository>();
        var warehouses = await stock.GetWarehousesAsync(Ct);
        var items = await stock.GetStockAsync([sku], null, Ct);

        var outcome = Reservation.Allocate(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), [new ReservationLine(sku, 1)], warehouses, items, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        stock.Add(outcome.Reservation!);

        return () => scope.ServiceProvider.GetRequiredService<IInventoryUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task GivenOutboxMessagesAsync(IServiceProvider provider, int count)
    {
        using var scope = provider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        for (var index = 0; index < count; index++)
        {
            outbox.Enqueue(new StockLevelLow { Sku = $"SKU-{index}", WarehouseCode = "WAW-1", Available = 1, ReorderThreshold = 5, DetectedAt = DateTimeOffset.UtcNow });
        }

        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().SaveChangesAsync(Ct);
    }
}
