using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Infrastructure.Persistence;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Inventory.Infrastructure.Catalog;

/// <summary>Catalog queries straight from SQL: no-tracking projections computed on the database side.</summary>
internal sealed class SqlCatalogReader(InventoryDbContext db) : ICatalogReader
{
    public async Task<ProductView?> GetProductAsync(string sku, CancellationToken cancellationToken)
    {
        var key = sku.ToUpperInvariant();

        var row = await db.Products.AsNoTracking()
            .Where(product => product.Id == key)
            .Select(product => new
            {
                product.Id,
                product.Name,
                product.Description,
                product.CategoryId,
                Amount = EF.Property<decimal>(product, "_priceAmount"),
                Currency = EF.Property<string>(product, "_priceCurrency"),
                product.IsActive,
                Available = db.StockItems.Where(item => item.Sku == product.Id).Sum(item => (int?)(item.OnHand - item.Reserved)) ?? 0,
                RowVersion = EF.Property<byte[]>(product, InventoryDbContext.RowVersion),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new ProductView(row.Id, row.Name, row.Description, row.CategoryId, new Money(row.Amount, row.Currency), row.IsActive, row.Available, RowVersionETag.Format(row.RowVersion)!);
    }

    public async Task<PagedResult<ProductSummary>> ListProductsAsync(int? categoryId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking().Where(product => product.IsActive && (categoryId == null || product.CategoryId == categoryId));

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderBy(product => product.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(product => new
            {
                product.Id,
                product.Name,
                Amount = EF.Property<decimal>(product, "_priceAmount"),
                Currency = EF.Property<string>(product, "_priceCurrency"),
                InStock = db.StockItems.Any(item => item.Sku == product.Id && item.OnHand - item.Reserved > 0),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<ProductSummary>(
            [.. rows.Select(row => new ProductSummary(row.Id, row.Name, new Money(row.Amount, row.Currency), row.InStock))],
            page,
            pageSize,
            total);
    }

    public async Task<IReadOnlyList<PriceView>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        string[] keys = [.. skus.Select(sku => sku.ToUpperInvariant()).Distinct()];

        var rows = await db.Products.AsNoTracking()
            .Where(product => keys.Contains(product.Id))
            .Select(product => new
            {
                product.Id,
                product.Name,
                Amount = EF.Property<decimal>(product, "_priceAmount"),
                Currency = EF.Property<string>(product, "_priceCurrency"),
                Available = product.IsActive && db.StockItems.Any(item => item.Sku == product.Id && item.OnHand - item.Reserved > 0),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new PriceView(row.Id, row.Name, new Money(row.Amount, row.Currency), row.Available))];
    }

    public async Task<IReadOnlyList<CountryView>> GetCountriesAsync(CancellationToken cancellationToken) =>
        await db.Countries.AsNoTracking()
            .OrderBy(country => country.Name)
            .Select(country => new CountryView(country.Code, country.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task InvalidateProductAsync(string sku, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Catalog with HybridCache: process memory (L1) in front of Azure Managed Redis (L2) in front of SQL.
/// </summary>
/// <remarks>
/// WHY: the catalog is the most frequently read data in the shop and it changes rarely. HybridCache gives three things
/// at once: L1 without network hops for hot products, an L2 shared by all replicas (a new replica after scale-out does
/// not start with an empty cache and does not flood SQL), and protection against a "stampede": when an entry expires,
/// only one request per process goes to the database and the rest wait for its result.
/// <para>
/// Invalidation: <c>RemoveAsync</c> removes the entry from this replica's L1 and from Redis. Other replicas may keep an
/// older copy in L1 until <see cref="LocalExpiration"/> passes. This is deliberately bounded staleness (at most
/// 30 seconds), acceptable for a product page. Prices for checkout do not go through the cache at all.
/// </para>
/// </remarks>
internal sealed class CachedCatalogReader(SqlCatalogReader inner, HybridCache cache) : ICatalogReader
{
    private const string ListsTag = "product-lists";

    private static readonly TimeSpan LocalExpiration = TimeSpan.FromSeconds(30);

    private static readonly HybridCacheEntryOptions ProductEntry = new() { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = LocalExpiration };

    private static readonly HybridCacheEntryOptions ListEntry = new() { Expiration = TimeSpan.FromMinutes(1), LocalCacheExpiration = LocalExpiration };

    private static readonly HybridCacheEntryOptions ReferenceEntry = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromMinutes(10) };

    public async Task<ProductView?> GetProductAsync(string sku, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            ProductKey(sku),
            (inner, sku),

            // A missing product is cached too (for the entry's lifetime). This protects SQL from repeated queries for
            // non-existent SKUs, e.g. from bots crawling the shop.
            static async (state, ct) => await state.inner.GetProductAsync(state.sku, ct).ConfigureAwait(false),
            ProductEntry,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<PagedResult<ProductSummary>> ListProductsAsync(int? categoryId, int page, int pageSize, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            $"products:{categoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"}:{page}:{pageSize}",
            (inner, categoryId, page, pageSize),
            static async (state, ct) => await state.inner.ListProductsAsync(state.categoryId, state.page, state.pageSize, ct).ConfigureAwait(false),
            ListEntry,
            [ListsTag],
            cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<PriceView>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken) =>
        inner.GetPricesAsync(skus, cancellationToken);

    public async Task<IReadOnlyList<CountryView>> GetCountriesAsync(CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            "ref:countries",
            inner,
            static async (reader, ct) => await reader.GetCountriesAsync(ct).ConfigureAwait(false),
            ReferenceEntry,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task InvalidateProductAsync(string sku, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ProductKey(sku), cancellationToken).ConfigureAwait(false);
        await cache.RemoveByTagAsync(ListsTag, cancellationToken).ConfigureAwait(false);
    }

    private static string ProductKey(string sku) => $"product:{sku.ToUpperInvariant()}";
}
