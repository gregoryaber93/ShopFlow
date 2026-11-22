using ShopFlow.Orders.Domain.Carts;
using static ShopFlow.Orders.UnitTests.TestData;

namespace ShopFlow.Orders.UnitTests.Domain;

public sealed class CartTests
{
    [Fact]
    public void Setting_quantity_adds_and_zero_removes()
    {
        var cart = Cart.Create(CustomerId, Now);

        cart.SetQuantity("SKU-1", 3, Now).IsSuccess.ShouldBeTrue();
        cart.Items["sku-1"].ShouldBe(3);

        cart.SetQuantity("SKU-1", 0, Now).IsSuccess.ShouldBeTrue();
        cart.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void Quantity_out_of_range_is_rejected(int quantity)
    {
        Cart.Create(CustomerId, Now).SetQuantity("SKU-1", quantity, Now).Error.Code.ShouldBe("cart.invalid_quantity");
    }

    [Fact]
    public void Cart_is_limited_to_fifty_distinct_items()
    {
        var cart = Cart.Create(CustomerId, Now);
        for (var index = 0; index < Cart.MaxDistinctItems; index++)
        {
            cart.SetQuantity($"SKU-{index}", 1, Now);
        }

        cart.SetQuantity("ONE-TOO-MANY", 1, Now).Error.Code.ShouldBe("cart.too_many_items");

        // Changing the quantity of an existing line is still possible.
        cart.SetQuantity("SKU-0", 5, Now).IsSuccess.ShouldBeTrue();
    }
}
