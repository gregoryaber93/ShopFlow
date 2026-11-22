using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.CustomerReadModels.Infrastructure;
using ShopFlow.Web.Authentication;
using ShopFlow.Web.Http;

namespace ShopFlow.CustomerReadModels.Api;

public static class ReadModelsPolicies
{
    public const string Customer = "Customer";

    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(Customer, policy => policy.RequireCustomer());
    }

    public static Guid GetCustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.GetObjectId(), out var id)
            ? id
            : throw new InvalidOperationException("The authenticated principal has no valid 'oid' claim.");
}

public static class CustomerOrderEndpoints
{
    /// <summary>Problem type for an order whose projection has not arrived yet.</summary>
    public const string ProjectionPendingProblem = "https://shopflow.example/problems/projection-pending";

    public static IEndpointRouteBuilder MapCustomerOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // WHY: "/me" instead of "/customers/{customerId}". The customer identifier comes only from the token, so there is nothing
        // in the URL to tamper with. An endpoint with the identifier in the path would need a separate check that it is the same
        // customer, and every forgotten condition is a leak of someone else's orders (IDOR).
        var orders = app.MapGroup("/v1/me/orders")
            .WithGroupName("v1")
            .WithTags("My orders")
            .RequireAuthorization(ReadModelsPolicies.Customer);

        orders.MapGet("/", ListAsync).WithName("ListMyOrders");
        orders.MapGet("/{orderId:guid}", GetAsync).WithName("GetMyOrder");
        orders.MapGet("/{orderId:guid}/invoice", GetInvoiceLinkAsync).WithName("GetMyInvoiceLink");

        return app;
    }

    private static async Task<Results<Ok<OrderListResponse>, ProblemHttpResult>> ListAsync(
        int? pageSize,
        string? pageToken,
        ClaimsPrincipal user,
        CustomerOrderQueries queries,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await queries.ListAsync(user.GetCustomerId(), pageSize ?? 20, pageToken, cancellationToken);
            http.Response.Headers.CacheControl = "private, no-cache";
            return TypedResults.Ok(new OrderListResponse([.. page.Items.Select(OrderSummaryResponse.From)], page.ContinuationToken));
        }
        catch (ArgumentException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid page token", detail: "Use the nextPageToken returned by the previous page.");
        }
    }

    private static async Task<IResult> GetAsync(
        Guid orderId,
        ClaimsPrincipal user,
        CustomerOrderQueries queries,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var lookup = await queries.GetAsync(user.GetCustomerId(), orderId, cancellationToken);
        if (lookup.View is not { } view)
        {
            return lookup.ProjectionPending ? ProjectionPending(http) : TypedResults.NotFound();
        }

        // WHY: "private" forbids shared caches (APIM, Front Door, corporate proxies) from storing the response. Without it an edge
        // cache could hand one customer's order to another. "no-cache" means the browser may keep a copy but must ask the server
        // before using it (If-None-Match → 304).
        http.Response.Headers.CacheControl = "private, no-cache";

        // Conditional GET: an application polling the status gets a bodiless 304 until the view changes.
        // On a Redis cache hit the whole response is produced without touching Cosmos DB.
        if (view.ETag is not null && http.Request.Headers.IfNoneMatch.ToString() == view.ETag)
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        http.Response.SetETag(view.ETag);
        return TypedResults.Ok(OrderDetailsResponse.From(view));
    }

    /// <remarks>
    /// WHY: a JSON response with a link, not a 302 redirect as with the label in Shipping. The customer application (SPA) calls
    /// the API with a token in the Authorization header, so it cannot simply open the API address in the browser. It gets the
    /// link and opens it itself. A warehouse employee opens the label in a browser with an Easy Auth session (cookie), so a
    /// redirect works there.
    /// </remarks>
    private static async Task<IResult> GetInvoiceLinkAsync(
        Guid orderId,
        ClaimsPrincipal user,
        CustomerOrderQueries queries,
        IInvoiceDownloadLinks links,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var lookup = await queries.GetAsync(user.GetCustomerId(), orderId, cancellationToken);
        if (lookup.View?.Invoice is not { } invoice)
        {
            return TypedResults.NotFound();
        }

        var link = await links.CreateAsync(invoice.BlobPath, cancellationToken);

        // A link with a SAS token must not land in any cache, including the browser's.
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new InvoiceLinkResponse(invoice.Number, link.Url, link.ExpiresAt));
    }

    /// <remarks>
    /// WHY: 404 with a Retry-After header and a dedicated problem type. The order exists (it has just been placed), but the
    /// read side does not have it yet. The customer application recognises the problem type and retries shortly instead of
    /// showing "order not found".
    /// </remarks>
    private static IResult ProjectionPending(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "2";
        return TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            type: ProjectionPendingProblem,
            title: "Order is not visible yet",
            detail: "The order was placed moments ago and is still being processed. Retry shortly.");
    }
}
