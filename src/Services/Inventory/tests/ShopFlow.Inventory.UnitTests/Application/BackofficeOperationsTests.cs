using ShopFlow.Inventory.Application.Catalog;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Inventory.UnitTests.Application;

public sealed class BackofficeOperationsTests : IDisposable
{
    private readonly InventoryTestHost _host = new();

    public BackofficeOperationsTests() => _host.State.WithProduct("SKU-1", 19.99m).WithStock("WAW-1", "SKU-1", onHand: 5);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Price_change_with_current_etag_is_saved_published_and_invalidates_the_cache()
    {
        var result = await ChangePriceAsync(24.99m, "\"v1\"");

        result.Value.ETag.ShouldBe("\"v2\"");
        _host.State.PublishedTypes().ShouldBe(["ProductPriceChanged"]);
        _host.Catalog.Invalidated.ShouldBe(["SKU-1"]);
    }

    [Fact]
    public async Task Price_change_based_on_a_stale_read_is_rejected()
    {
        await Should.ThrowAsync<ConcurrencyConflictException>(() => ChangePriceAsync(24.99m, "\"v0\""));
        _host.State.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Same_receipt_sent_twice_increases_stock_once()
    {
        var receiptId = Guid.NewGuid();

        var first = await ReceiveAsync(receiptId, 10);
        var second = await ReceiveAsync(receiptId, 10);

        first.Value.Duplicate.ShouldBeFalse();
        second.Value.Duplicate.ShouldBeTrue();
        _host.State.StockOf("WAW-1", "SKU-1").OnHand.ShouldBe(15);
    }

    private Task<Result<PriceChanged>> ChangePriceAsync(decimal amount, string etag) =>
        _host.InScopeAsync<BackofficeOperations, Result<PriceChanged>>(operations =>
            operations.ChangePriceAsync("SKU-1", new Money(amount, "PLN"), etag, TestContext.Current.CancellationToken));

    private Task<Result<ReceiptOutcome>> ReceiveAsync(Guid receiptId, int quantity) =>
        _host.InScopeAsync<BackofficeOperations, Result<ReceiptOutcome>>(operations =>
            operations.ReceiveStockAsync(receiptId, "WAW-1", "SKU-1", quantity, "wms", TestContext.Current.CancellationToken));
}
