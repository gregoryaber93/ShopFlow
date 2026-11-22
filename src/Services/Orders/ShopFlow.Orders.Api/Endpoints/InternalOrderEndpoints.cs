using ShopFlow.Orders.Api.Http;
using ShopFlow.Orders.Application.Orders;
using ShopFlow.SharedKernel.Results;
using ShopFlow.Web.Http;

namespace ShopFlow.Orders.Api.Endpoints;

/// <summary>
/// Endpoints for order-saga. Not exposed through API Management.
/// </summary>
/// <remarks>
/// AZURE: the saga calls orders-api directly through the App Service private endpoint, from the VNet
/// (the function app's VNet integration). The /internal path does not exist in the APIM API definition, so
/// it is unreachable from the internet even with a valid token. The second layer of protection is the app
/// role required by the <see cref="OrdersPolicies.InternalWrite"/> policy.
/// <para>
/// Call protocol: GET for the current ETag, then PUT with If-Match. A 412 response means the saga should read
/// the order again, check whether the operation has already been performed, and retry.
/// </para>
/// </remarks>
public static class InternalOrderEndpoints
{
    public static IEndpointRouteBuilder MapInternalOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/orders")
            .WithGroupName("internal")
            .WithTags("Internal")
            .RequireAuthorization(OrdersPolicies.InternalWrite);

        group.MapGet("/{orderId:guid}", async (Guid orderId, GetOrderHandler handler, HttpContext http, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(orderId, customerId: null, ct);
            if (!result.TryGetValue(out var order))
            {
                return result.Error.ToProblem();
            }

            http.Response.SetETag(order.ConcurrencyToken);
            return TypedResults.Ok(OrderResponse.From(order));
        });

        group.MapPut("/{orderId:guid}/confirmation", (Guid orderId, ConfirmOrderRequest? request, SagaOrderOperations operations, HttpContext http, CancellationToken ct) =>
            WithIfMatch(http, etag => operations.ConfirmAsync(orderId, etag, request?.TrackingNumber, ct)));

        group.MapPut("/{orderId:guid}/rejection", (Guid orderId, RejectOrderRequest request, SagaOrderOperations operations, HttpContext http, CancellationToken ct) =>
            WithIfMatch(http, etag => operations.RejectAsync(orderId, etag, request.ReasonCode, request.Description, ct)));

        group.MapPut("/{orderId:guid}/cancellation", (Guid orderId, SagaOrderOperations operations, HttpContext http, CancellationToken ct) =>
            WithIfMatch(http, etag => operations.CompleteCancellationAsync(orderId, etag, ct)));

        return app;
    }

    private static async Task<IResult> WithIfMatch(HttpContext http, Func<string, Task<Result<OrderMutation>>> operation)
    {
        if (!http.Request.TryGetIfMatch(out var etag))
        {
            return ConcurrencyHttpExtensions.PreconditionRequired();
        }

        var result = await operation(etag);

        return result.Match<IResult>(
            mutation =>
            {
                http.Response.SetETag(mutation.ETag);
                return TypedResults.Ok(OrderResponse.From(mutation.Order));
            },
            error => error.ToProblem());
    }
}
