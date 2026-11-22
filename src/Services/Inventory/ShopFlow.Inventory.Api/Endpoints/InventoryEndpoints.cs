using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Application.Catalog;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.Web.Authentication;
using ShopFlow.Web.Http;

namespace ShopFlow.Inventory.Api.Endpoints;

public static class InventoryPolicies
{
    /// <summary>App role granted to the orders-api identity (prices for checkout).</summary>
    public const string PricesReader = "Catalog.Prices.Read";

    /// <summary>App role of the warehouse management system (WMS) that reports stock receipts.</summary>
    public const string ReceiptsWriter = "Stock.Receipts.Write";

    /// <summary>Role of catalog department employees (users, not applications).</summary>
    public const string CatalogManager = "Catalog.Manager";

    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(PricesReader, policy => policy.RequireApplicationRole(PricesReader));
        options.AddPolicy(ReceiptsWriter, policy => policy.RequireApplicationRole(ReceiptsWriter));
        options.AddPolicy(CatalogManager, policy => policy.RequireWorkforceUserRole(CatalogManager));
    }
}

public sealed record MoneyResponse(decimal Amount, string Currency)
{
    public static MoneyResponse From(Money money) => new(money.Amount, money.Currency);
}

public sealed record ProductResponse(string Sku, string Name, string? Description, int CategoryId, MoneyResponse Price, bool IsActive, int Available);

public sealed record ProductSummaryResponse(string Sku, string Name, MoneyResponse Price, bool InStock);

/// <summary>Shape agreed with the consumer (orders-api, CatalogPricingClient). Changing it requires Orders' consent (Pact test).</summary>
public sealed record PriceItemResponse(string Sku, string ProductName, MoneyResponse UnitPrice, bool IsAvailable);

public sealed record PricesResponse(IReadOnlyList<PriceItemResponse> Items);

public sealed record ChangePriceRequest(decimal Amount, string Currency);

public sealed record StockReceiptRequest(Guid ReceiptId, string WarehouseCode, string Sku, int Quantity);

public static class InventoryEndpoints
{
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // The catalog is public: browsing the shop requires no sign-in. Front Door caches these responses at the edge
        // according to the Cache-Control headers (the /catalog/* route in infra/modules/edge/front-door.bicep).
        var catalog = app.MapGroup("/v1/catalog").WithGroupName("v1").WithTags("Catalog").AllowAnonymous();

        catalog.MapGet("/products", async (int? categoryId, int? page, int? pageSize, ICatalogReader reader, HttpContext http, CancellationToken ct) =>
        {
            var result = await reader.ListProductsAsync(categoryId, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, MaxPageSize), ct);
            http.Response.Headers.CacheControl = "public, max-age=60";
            return TypedResults.Ok(new
            {
                Items = result.Items.Select(item => new ProductSummaryResponse(item.Sku, item.Name, MoneyResponse.From(item.Price), item.InStock)),
                result.Page,
                result.PageSize,
                result.TotalCount,
            });
        });

        catalog.MapGet("/products/{sku}", async (string sku, ICatalogReader reader, HttpContext http, CancellationToken ct) =>
        {
            var product = await reader.GetProductAsync(sku, ct);
            if (product is null)
            {
                return Results.NotFound();
            }

            http.Response.SetETag(product.ETag);
            http.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok(new ProductResponse(product.Sku, product.Name, product.Description, product.CategoryId, MoneyResponse.From(product.Price), product.IsActive, product.Available));
        });

        app.MapGet("/v1/reference/countries", async (ICatalogReader reader, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return TypedResults.Ok(await reader.GetCountriesAsync(ct));
        }).WithGroupName("v1").WithTags("Reference").AllowAnonymous();

        // Prices for checkout: service-to-service only, outside APIM. Always from the database (no cache).
        app.MapGet("/internal/catalog/prices", async (string[] sku, ICatalogReader reader, CancellationToken ct) =>
        {
            if (sku.Length is 0 or > 50)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Provide between 1 and 50 'sku' query parameters.");
            }

            var prices = await reader.GetPricesAsync(sku, ct);
            return Results.Ok(new PricesResponse([.. prices.Select(price => new PriceItemResponse(price.Sku, price.ProductName, MoneyResponse.From(price.UnitPrice), price.IsAvailable))]));
        }).WithGroupName("internal").WithTags("Internal").RequireAuthorization(InventoryPolicies.PricesReader);

        var admin = app.MapGroup("/v1/admin").WithGroupName("v1").WithTags("Backoffice");

        admin.MapPut("/products/{sku}/price", async (string sku, ChangePriceRequest request, BackofficeOperations operations, HttpContext http, CancellationToken ct) =>
        {
            if (!http.Request.TryGetIfMatch(out var etag))
            {
                return ConcurrencyHttpExtensions.PreconditionRequired();
            }

            var result = await operations.ChangePriceAsync(sku, new Money(request.Amount, request.Currency), etag, ct);
            return result.Match(
                changed =>
                {
                    http.Response.SetETag(changed.ETag);
                    return Results.Ok(new { changed.Sku, Price = MoneyResponse.From(changed.Price) });
                },
                error => error.ToProblem());
        }).RequireAuthorization(InventoryPolicies.CatalogManager);

        admin.MapPost("/stock/receipts", async (StockReceiptRequest request, ClaimsPrincipal user, BackofficeOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ReceiveStockAsync(
                request.ReceiptId,
                request.WarehouseCode,
                request.Sku,
                request.Quantity,
                user.GetObjectId() ?? "unknown",
                ct);

            // 201 on the first receipt, 200 on a repeat: the client (WMS) knows the retry was safe.
            return result.Match(
                outcome => outcome.Duplicate
                    ? Results.Ok(outcome)
                    : Results.Created($"/v1/admin/stock/receipts/{outcome.ReceiptId}", outcome),
                error => error.ToProblem());
        }).RequireAuthorization(InventoryPolicies.ReceiptsWriter);

        return app;
    }
}
