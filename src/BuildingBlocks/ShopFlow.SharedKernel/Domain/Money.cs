using System.Globalization;

namespace ShopFlow.SharedKernel.Domain;

/// <summary>Amount in a specific currency. Arithmetic across different currencies is an error.</summary>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new DomainException("money.invalid_currency", $"'{currency}' is not an ISO 4217 currency code.");
        }

        // Amounts are kept with minor-unit precision. Banker's rounding
        // does not accumulate error when many lines are summed.
        Amount = decimal.Round(amount, 2, MidpointRounding.ToEven);
        Currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Zero(string currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>
    /// Amount in minor units (grosze, cents). Payment provider APIs expect amounts in this form.
    /// </summary>
    public long ToMinorUnits() => (long)(Amount * 100m);

    public static Money FromMinorUnits(long minorUnits, string currency) => new(minorUnits / 100m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator *(Money money, int quantity) => new(money.Amount * quantity, money.Currency);

    public static Money operator *(Money money, decimal factor) => new(money.Amount * factor, money.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {Currency}");

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new DomainException(
                "money.currency_mismatch",
                $"Cannot combine amounts in {left.Currency} and {right.Currency}.");
        }
    }
}
