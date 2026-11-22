using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Carts;
using ShopFlow.SharedKernel.Persistence;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Application.Carts;

/// <summary>
/// Cart operations with optimistic concurrency resolved on the server side.
/// </summary>
/// <remarks>
/// WHY: two different approaches to the same technique in one service. An order requires If-Match from the
/// client, because changing a status based on a stale view is a business error (e.g. cancelling an order that
/// has just been confirmed). In a cart a conflict only means the customer changed two lines from two browser
/// tabs. Setting the quantity of one SKU is independent of the rest of the cart, so the server retries the
/// read-modify-write itself and the client never sees a 412.
/// </remarks>
public sealed class CartService(ICartRepository carts, TimeProvider timeProvider)
{
    private const int MaxAttempts = 3;

    public Task<Cart?> GetAsync(Guid customerId, CancellationToken cancellationToken) =>
        carts.GetAsync(customerId, cancellationToken);

    public async Task<Result<Cart>> SetItemQuantityAsync(Guid customerId, string sku, int quantity, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var cart = await carts.GetAsync(customerId, cancellationToken).ConfigureAwait(false) ?? Cart.Create(customerId, now);

            var result = cart.SetQuantity(sku, quantity, now);
            if (result.IsFailure)
            {
                return result.Error;
            }

            try
            {
                await carts.SaveAsync(cart, cancellationToken).ConfigureAwait(false);
                return cart;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // Someone changed the cart between our read and our write. We read it again
                // and apply the same change to the fresh state.
            }
        }
    }
}
