using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.BuildingBlocks.Tests.SharedKernel;

public sealed class MoneyTests
{
    [Fact]
    public void Amounts_are_rounded_half_to_even()
    {
        new Money(10.005m, "PLN").Amount.ShouldBe(10.00m);
        new Money(10.015m, "PLN").Amount.ShouldBe(10.02m);
    }

    [Fact]
    public void Currency_is_normalized_to_upper_case()
    {
        new Money(1m, "pln").Currency.ShouldBe("PLN");
    }

    [Fact]
    public void Adding_different_currencies_is_a_domain_error()
    {
        var exception = Should.Throw<DomainException>(() => new Money(1m, "PLN") + new Money(1m, "EUR"));

        exception.Code.ShouldBe("money.currency_mismatch");
    }

    [Fact]
    public void Minor_units_round_trip()
    {
        var money = new Money(123.45m, "EUR");

        money.ToMinorUnits().ShouldBe(12345);
        Money.FromMinorUnits(12345, "EUR").ShouldBe(money);
    }

    [Fact]
    public void Invalid_currency_code_is_rejected()
    {
        Should.Throw<DomainException>(() => new Money(1m, "ZLOTY"));
    }
}
