using System.Text.Json.Serialization;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Carts;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Orders.Infrastructure.Cosmos;

public sealed class CartDocument : CosmosDocument
{
    public const string DocumentType = "cart";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    public required string CustomerId { get; init; }

    public required Dictionary<string, int> Items { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class CosmosCartRepository(OrdersContainers containers, ICosmosUnitOfWorkFactory unitOfWork) : ICartRepository
{
    /// <summary>
    /// AZURE: TTL counts from the document's last write (the <c>_ts</c> system field), not from its creation.
    /// Every cart change therefore moves the expiry date, and an abandoned cart disappears on its own after 30 days
    /// without a clean-up job and without spending request RU (TTL deletion runs in the background).
    /// </summary>
    private static readonly int CartTimeToLiveSeconds = (int)TimeSpan.FromDays(30).TotalSeconds;

    public async Task<Cart?> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var key = customerId.ToString("D");
        var document = await containers.Carts.ReadOrDefaultAsync<CartDocument>(key, key, cancellationToken).ConfigureAwait(false);

        return document is null
            ? null
            : Cart.Restore(customerId, document.Items, document.UpdatedAt, document.ETag);
    }

    public async Task SaveAsync(Cart cart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cart);

        var key = cart.CustomerId.ToString("D");
        var document = new CartDocument
        {
            Id = key,
            CustomerId = key,
            Items = new Dictionary<string, int>(cart.Items),
            UpdatedAt = cart.UpdatedAt,
            TimeToLive = CartTimeToLiveSeconds,
        };

        var work = unitOfWork.Create(containers.Carts, OrdersContainers.CartsPartitionKey, key);
        work = cart.ConcurrencyToken is null ? work.Create(document) : work.Replace(document, cart.ConcurrencyToken);

        try
        {
            var result = await work.CommitAsync(cancellationToken).ConfigureAwait(false);
            cart.SetConcurrencyToken(result.GetETag(key));
        }
        catch (DocumentAlreadyExistsException exception)
        {
            // Two first writes of a cart at once: from the application's point of view it is the same version conflict.
            throw new ConcurrencyConflictException(nameof(Cart), key, exception);
        }
    }

    public async Task DeleteAsync(Cart cart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cart);

        var key = cart.CustomerId.ToString("D");

        // Delete with If-Match: if the customer added something to the cart in the meantime (e.g. in a second tab),
        // we do not erase that change together with the ordered lines.
        await unitOfWork
            .Create(containers.Carts, OrdersContainers.CartsPartitionKey, key)
            .Delete(key, CartDocument.DocumentType, cart.ConcurrencyToken)
            .CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
