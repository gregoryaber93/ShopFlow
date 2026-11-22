using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Infrastructure.Catalog;

public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// Internal address of inventory-api, e.g.
    /// <c>https://ca-shopflow-inventory-api-prod-weu.internal.happyhill-1a2b3c4d.westeurope.azurecontainerapps.io/</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: the Container Apps environment is internal (no public IP). Its default domain is resolved by a private
    /// DNS zone linked to the VNet, and App Service sends outbound traffic there through VNet integration. The traffic
    /// does not go through Front Door or APIM: it is a service-to-service call secured with an Entra ID token carrying
    /// the Catalog.Prices.Read role.
    /// </remarks>
    [Required]
    public Uri? InventoryApiBaseUrl { get; set; }

    /// <summary>Token scope for inventory-api, e.g. <c>api://shopflow-inventory/.default</c>.</summary>
    [Required]
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// How long a cached price may stand in for the catalog when the catalog does not respond.
    /// </summary>
    /// <remarks>
    /// WHY: a business decision, not a technical one. For 15 minutes it is better to accept an order at a price from
    /// a moment ago than to reject checkout because the catalog is down. Beyond that, the risk of selling at a stale
    /// price (e.g. after a promotion ended) is no longer acceptable.
    /// </remarks>
    public TimeSpan StalePriceTolerance { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Prices from inventory-api, with a Redis cache as a fallback during outages ("stale-if-error").
/// </summary>
/// <remarks>
/// WHY: at checkout the cache is there for resilience, not speed. Normally we always ask the catalog, because the
/// price at purchase must be current, and we store every answer in Redis. Only when the catalog does not respond
/// (after retries and with the circuit breaker open) do we fall back to the last known price.
/// This is the opposite of a typical "cache-aside", where the cache is read first.
/// </remarks>
internal sealed partial class CatalogPricingClient(
    HttpClient http,
    IDistributedCache cache,
    Microsoft.Extensions.Options.IOptions<CatalogOptions> options,
    ILogger<CatalogPricingClient> logger) : ICatalogPricing
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, CatalogPrice>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skus);

        try
        {
            var prices = await FetchAsync(skus, cancellationToken).ConfigureAwait(false);
            await RememberAsync(prices.Values, cancellationToken).ConfigureAwait(false);
            return prices;
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return await FallbackAsync(skus, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, CatalogPrice>> FetchAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        // GET with the SKU list in the query string, not POST with a body. GET is idempotent, so the resilience
        // pipeline may retry it safely (see HttpResilienceExtensions.ShouldRetrySafely).
        // The 50-line cart limit keeps the URL far below length limits.
        var query = string.Join('&', skus.Select(sku => $"sku={Uri.EscapeDataString(sku)}"));
        var response = await http
            .GetFromJsonAsync<CatalogPricesResponse>($"internal/catalog/prices?{query}", Json, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new HttpRequestException("Catalog returned an empty body.");

        return response.Items.ToDictionary(
            item => item.Sku,
            item => new CatalogPrice(item.Sku, item.ProductName, new Money(item.UnitPrice.Amount, item.UnitPrice.Currency), item.IsAvailable),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task RememberAsync(IEnumerable<CatalogPrice> prices, CancellationToken cancellationToken)
    {
        var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.StalePriceTolerance };

        try
        {
            foreach (var price in prices)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(CachedPrice.From(price), Json);
                await cache.SetAsync(CacheKey(price.Sku), bytes, entryOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unavailable Redis must not block checkout: we only lose the fallback for a future outage.
            LogCacheWriteFailed(exception);
        }
    }

    private async Task<IReadOnlyDictionary<string, CatalogPrice>> FallbackAsync(
        IReadOnlyCollection<string> skus,
        Exception cause,
        CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, CatalogPrice>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var sku in skus)
            {
                var bytes = await cache.GetAsync(CacheKey(sku), cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    throw new CatalogUnavailableException($"Catalog is unavailable and no recent price is cached for SKU '{sku}'.", cause);
                }

                prices[sku] = JsonSerializer.Deserialize<CachedPrice>(bytes, Json)!.ToCatalogPrice();
            }
        }
        catch (Exception exception) when (exception is not (CatalogUnavailableException or OperationCanceledException))
        {
            throw new CatalogUnavailableException("Catalog and the price cache are both unavailable.", exception);
        }

        LogServedFromCache(cause, prices.Count);
        return prices;
    }

    private static bool IsDependencyFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or JsonException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static string CacheKey(string sku) => $"pricing:{sku.ToUpperInvariant()}";

    private sealed record CatalogPricesResponse(IReadOnlyList<CatalogPriceItem> Items);

    private sealed record CatalogPriceItem(string Sku, string ProductName, PriceAmount UnitPrice, bool IsAvailable);

    private sealed record PriceAmount(decimal Amount, string Currency);

    private sealed record CachedPrice(string Sku, string ProductName, decimal Amount, string Currency, bool IsAvailable)
    {
        public static CachedPrice From(CatalogPrice price) =>
            new(price.Sku, price.ProductName, price.UnitPrice.Amount, price.UnitPrice.Currency, price.IsAvailable);

        public CatalogPrice ToCatalogPrice() => new(Sku, ProductName, new Money(Amount, Currency), IsAvailable);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog unavailable; served {Count} price(s) from the Redis cache")]
    private partial void LogServedFromCache(Exception cause, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write prices to the Redis cache")]
    private partial void LogCacheWriteFailed(Exception exception);
}
