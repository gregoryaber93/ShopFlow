using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ShopFlow.Orders.Api.Http;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Application.Orders;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.Web.Http;
using ShopFlow.Web.Idempotency;

namespace ShopFlow.Orders.Api.Endpoints;

/// <summary>
/// Public orders API, exposed through API Management under /orders/v1 and /orders/v2.
/// </summary>
/// <remarks>
/// AZURE: APIM keeps the version segment in the backend path, so versioning exists in two places. In APIM it
/// is a "version set" with a separate OpenAPI definition and policies for each version (e.g. a different rate
/// limit). In the service it is two route groups sharing the handlers. Both versions run side by side until the
/// Sunset date.
/// </remarks>
public static class PublicOrderEndpoints
{
    /// <summary>v1 retirement date, announced to clients in the Sunset header (RFC 8594).</summary>
    private static readonly DateTimeOffset V1Sunset = new(2027, 6, 30, 0, 0, 0, TimeSpan.Zero);

    public static IEndpointRouteBuilder MapPublicOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1/orders")
            .WithGroupName("v1")
            .WithTags("Orders")
            .RequireAuthorization(OrdersPolicies.Customer);

        v1.MapPost("/", SubmitV1)
            .RequireIdempotencyKey()
            .AddEndpointFilter(AnnounceDeprecation)
            .WithName("SubmitOrderV1");
        v1.MapGet("/{orderId:guid}", GetOrder).WithName("GetOrderV1");
        v1.MapPost("/{orderId:guid}/cancellation", RequestCancellation).WithName("CancelOrderV1");

        var v2 = app.MapGroup("/v2/orders")
            .WithGroupName("v2")
            .WithTags("Orders")
            .RequireAuthorization(OrdersPolicies.Customer);

        v2.MapPost("/", SubmitV2).RequireIdempotencyKey().WithName("SubmitOrderV2");
        v2.MapGet("/{orderId:guid}", GetOrder).WithName("GetOrderV2");
        v2.MapPost("/{orderId:guid}/cancellation", RequestCancellation).WithName("CancelOrderV2");

        return app;
    }

    private static Task<IResult> SubmitV1(
        SubmitOrderRequestV1 request,
        ClaimsPrincipal user,
        SubmitOrderHandler handler,
        IValidator<SubmitOrderCommand> validator,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var command = new SubmitOrderCommand(
            user.GetCustomerId(),
            new DeliveryInput(DeliveryMethod.Courier, request.ShippingAddress?.ToDomain(), null),
            request.PaymentMethodToken);

        return SubmitAsync(command, "v1", handler, validator, http, cancellationToken);
    }

    private static Task<IResult> SubmitV2(
        SubmitOrderRequestV2 request,
        ClaimsPrincipal user,
        SubmitOrderHandler handler,
        IValidator<SubmitOrderCommand> validator,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var delivery = request.Delivery;
        var command = new SubmitOrderCommand(
            user.GetCustomerId(),
            new DeliveryInput(
                delivery.Method == DeliveryMethodDto.Courier ? DeliveryMethod.Courier : DeliveryMethod.PickupPoint,
                delivery.Address?.ToDomain(),
                delivery.PickupPointId),
            request.PaymentMethodToken);

        return SubmitAsync(command, "v2", handler, validator, http, cancellationToken);
    }

    private static async Task<IResult> SubmitAsync(
        SubmitOrderCommand command,
        string version,
        SubmitOrderHandler handler,
        IValidator<SubmitOrderCommand> validator,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return TypedResults.ValidationProblem(validation.ToDictionary());
        }

        try
        {
            var result = await handler.HandleAsync(command, cancellationToken);

            return result.Match(
                order =>
                {
                    http.Response.SetETag(order.ETag);

                    // 202, not 201: the order is accepted, but the saga decides its fate (reservation, payment,
                    // shipment). The client tracks the status at the address from the Location header.
                    return Results.Accepted(
                        $"/{version}/orders/{order.OrderId}",
                        new OrderAcceptedResponse(order.OrderId, order.OrderNumber, order.Status));
                },
                error => error.ToProblem());
        }
        catch (CatalogUnavailableException)
        {
            http.Response.Headers.RetryAfter = "30";
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Prices cannot be confirmed right now",
                detail: "The product catalog is temporarily unavailable. Retry with the same Idempotency-Key.");
        }
    }

    private static async Task<IResult> GetOrder(
        Guid orderId,
        ClaimsPrincipal user,
        GetOrderHandler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(orderId, user.GetCustomerId(), cancellationToken);
        if (!result.TryGetValue(out var order))
        {
            return result.Error.ToProblem();
        }

        // Conditional GET: a client polling the status (after the 202) gets a bodiless 304 until the order
        // changes. The Cosmos read still happens, but we save the transfer.
        if (http.Request.Headers.IfNoneMatch.ToString() == order.ConcurrencyToken)
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        http.Response.SetETag(order.ConcurrencyToken);
        return TypedResults.Ok(OrderResponse.From(order));
    }

    private static async Task<IResult> RequestCancellation(
        Guid orderId,
        CancelOrderRequest? request,
        ClaimsPrincipal user,
        RequestCancellationHandler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!http.Request.TryGetIfMatch(out var etag))
        {
            return ConcurrencyHttpExtensions.PreconditionRequired();
        }

        var result = await handler.HandleAsync(orderId, user.GetCustomerId(), etag, request?.Reason, cancellationToken);

        return result.Match<IResult>(
            mutation =>
            {
                http.Response.SetETag(mutation.ETag);

                // 202: the request is accepted, but the saga decides about the cancellation (it may confirm the order first).
                return TypedResults.Accepted($"/v2/orders/{orderId}", OrderResponse.From(mutation.Order));
            },
            error => error.ToProblem());
    }

    /// <summary>Deprecation (RFC 9745) and Sunset (RFC 8594) headers plus a link to the successor.</summary>
    private static async ValueTask<object?> AnnounceDeprecation(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Response.Headers;
        headers["Deprecation"] = "@1767225600";
        headers["Sunset"] = V1Sunset.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        headers.Link = "</v2/orders>; rel=\"successor-version\"";
        return await next(context);
    }
}
