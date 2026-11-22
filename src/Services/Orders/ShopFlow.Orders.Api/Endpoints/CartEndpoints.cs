using System.Security.Claims;
using ShopFlow.Orders.Api.Http;
using ShopFlow.Orders.Application.Carts;
using ShopFlow.Web.Http;

namespace ShopFlow.Orders.Api.Endpoints;

public static class CartEndpoints
{
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder app)
    {
        var cart = app.MapGroup("/v1/cart")
            .WithGroupName("v1")
            .WithTags("Cart")
            .RequireAuthorization(OrdersPolicies.Customer);

        cart.MapGet("/", GetCart);

        // PUT is idempotent by nature ("set the quantity to N"), so it does not need an idempotency key.
        cart.MapPut("/items/{sku}", (string sku, SetCartItemRequest request, ClaimsPrincipal user, CartService carts, HttpContext http, CancellationToken ct) =>
            SetQuantityAsync(sku, request.Quantity, user, carts, http, ct));
        cart.MapDelete("/items/{sku}", (string sku, ClaimsPrincipal user, CartService carts, HttpContext http, CancellationToken ct) =>
            SetQuantityAsync(sku, 0, user, carts, http, ct));

        return app;
    }

    private static async Task<IResult> GetCart(ClaimsPrincipal user, CartService carts, HttpContext http, CancellationToken cancellationToken)
    {
        var cart = await carts.GetAsync(user.GetCustomerId(), cancellationToken);
        if (cart is null)
        {
            return TypedResults.Ok(CartResponse.Empty);
        }

        http.Response.SetETag(cart.ConcurrencyToken);
        return TypedResults.Ok(CartResponse.From(cart));
    }

    private static async Task<IResult> SetQuantityAsync(
        string sku,
        int quantity,
        ClaimsPrincipal user,
        CartService carts,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await carts.SetItemQuantityAsync(user.GetCustomerId(), sku, quantity, cancellationToken);

        return result.Match<IResult>(
            cart =>
            {
                http.Response.SetETag(cart.ConcurrencyToken);
                return TypedResults.Ok(CartResponse.From(cart));
            },
            error => error.ToProblem());
    }
}
