namespace ShopFlow.Contracts.Common;

// WHY: types shared between contracts are copied into the contracts package instead of being
// imported from ShopFlow.SharedKernel. A contract is a public data schema; a domain type
// (e.g. Money with arithmetic and validation) is an implementation detail of a single service.

/// <summary>Amount with an ISO 4217 currency.</summary>
public sealed record MoneyDto
{
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}

/// <summary>Delivery address.</summary>
public sealed record AddressDto
{
    public required string RecipientName { get; init; }

    public required string Line1 { get; init; }

    public string? Line2 { get; init; }

    public required string City { get; init; }

    public required string PostalCode { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public required string CountryCode { get; init; }

    public string? Phone { get; init; }
}

/// <summary>Order line with the price frozen at the moment the order was placed.</summary>
public sealed record OrderLineDto
{
    public required string Sku { get; init; }

    public required string ProductName { get; init; }

    public required int Quantity { get; init; }

    public required MoneyDto UnitPrice { get; init; }
}

/// <summary>Quantity of a SKU, without a price. Used in warehouse and shipping commands.</summary>
public sealed record StockLineDto
{
    public required string Sku { get; init; }

    public required int Quantity { get; init; }
}
