using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Domain.Stock;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Inventory.Application.Catalog;

public sealed record PriceChanged(string Sku, Money Price, string ETag);

public sealed record ReceiptOutcome(Guid ReceiptId, bool Duplicate, int OnHand);

/// <summary>Back-office operations on the catalog and stock levels.</summary>
public sealed class BackofficeOperations(
    IProductRepository products,
    IStockRepository stock,
    IInventoryUnitOfWork unitOfWork,
    InventoryEventPublisher publisher,
    ICatalogReader catalog,
    TimeProvider timeProvider)
{
    private const int DefaultReorderThreshold = 10;

    /// <summary>Price change with optimistic concurrency: the ETag is the product row's rowversion.</summary>
    public async Task<Result<PriceChanged>> ChangePriceAsync(string sku, Money newPrice, string ifMatch, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(sku, cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            return Error.NotFound("product.not_found", $"Product {sku} does not exist.");
        }

        if (!string.Equals(product.ConcurrencyToken, ifMatch, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException("Product", sku);
        }

        var result = product.ChangePrice(newPrice, timeProvider.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error;
        }

        publisher.Publish(product);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Invalidate after the transaction commits: in the reverse order another request could read the old price from
        // the database in the meantime and put it back into the cache.
        await catalog.InvalidateProductAsync(product.Sku, cancellationToken).ConfigureAwait(false);

        return new PriceChanged(product.Sku, product.Price, product.ConcurrencyToken!);
    }

    /// <summary>Stock receipt reported by the warehouse system. Idempotent per receipt identifier.</summary>
    public async Task<Result<ReceiptOutcome>> ReceiveStockAsync(
        Guid receiptId,
        string warehouseCode,
        string sku,
        int quantity,
        string receivedBy,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0)
        {
            return Error.Validation("receipt.invalid_quantity", "Quantity must be positive.");
        }

        var normalizedSku = sku.ToUpperInvariant();
        var items = await stock.GetStockAsync([normalizedSku], warehouseCode, cancellationToken).ConfigureAwait(false);
        var item = items.SingleOrDefault();

        if (await stock.ReceiptExistsAsync(receiptId, cancellationToken).ConfigureAwait(false))
        {
            return new ReceiptOutcome(receiptId, Duplicate: true, item?.OnHand ?? 0);
        }

        if (item is null)
        {
            item = new StockItem(warehouseCode, normalizedSku, 0, DefaultReorderThreshold);
            stock.Add(item);
        }

        item.Receive(quantity);
        stock.Add(new StockReceipt(receiptId, warehouseCode, normalizedSku, quantity, timeProvider.GetUtcNow(), receivedBy));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await catalog.InvalidateProductAsync(normalizedSku, cancellationToken).ConfigureAwait(false);

        return new ReceiptOutcome(receiptId, Duplicate: false, item.OnHand);
    }
}
