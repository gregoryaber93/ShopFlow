using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ShopFlow.Inventory.Infrastructure.Persistence;

// Reference data (ref schema). It changes rarely and together with deployments, so it is part of the migrations
// (HasData) rather than an import. A VAT rate change is a new migration, reviewed in a pull request.

public sealed record Country(string Code, string Name);

public sealed record Currency(string Code, string Name, int MinorUnits);

public sealed record TaxRate(string CountryCode, string Category, decimal Rate);

public sealed record Carrier(string Code, string Name);

internal sealed class ReferenceDataConfiguration :
    IEntityTypeConfiguration<Country>,
    IEntityTypeConfiguration<Currency>,
    IEntityTypeConfiguration<TaxRate>,
    IEntityTypeConfiguration<Carrier>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.ToTable("Countries", "ref");
        builder.HasKey(country => country.Code);
        builder.Property(country => country.Code).HasMaxLength(2).IsFixedLength().IsUnicode(false);
        builder.Property(country => country.Name).HasMaxLength(100);
        builder.HasData(
            new Country("PL", "Poland"),
            new Country("DE", "Germany"),
            new Country("CZ", "Czechia"),
            new Country("SK", "Slovakia"),
            new Country("LT", "Lithuania"));
    }

    public void Configure(EntityTypeBuilder<Currency> builder)
    {
        builder.ToTable("Currencies", "ref");
        builder.HasKey(currency => currency.Code);
        builder.Property(currency => currency.Code).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(currency => currency.Name).HasMaxLength(100);
        builder.HasData(new Currency("PLN", "Polish zloty", 2), new Currency("EUR", "Euro", 2), new Currency("CZK", "Czech koruna", 2));
    }

    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        builder.ToTable("TaxRates", "ref");
        builder.HasKey(rate => new { rate.CountryCode, rate.Category });
        builder.Property(rate => rate.CountryCode).HasMaxLength(2).IsFixedLength().IsUnicode(false);
        builder.Property(rate => rate.Category).HasMaxLength(32).IsUnicode(false);
        builder.Property(rate => rate.Rate).HasPrecision(5, 4);
        builder.HasData(
            new TaxRate("PL", "standard", 0.23m),
            new TaxRate("PL", "reduced", 0.08m),
            new TaxRate("DE", "standard", 0.19m),
            new TaxRate("DE", "reduced", 0.07m));
    }

    public void Configure(EntityTypeBuilder<Carrier> builder)
    {
        builder.ToTable("Carriers", "ref");
        builder.HasKey(carrier => carrier.Code);
        builder.Property(carrier => carrier.Code).HasMaxLength(16).IsUnicode(false);
        builder.Property(carrier => carrier.Name).HasMaxLength(100);
        builder.HasData(new Carrier("DPD", "DPD Polska"), new Carrier("INPOST", "InPost"), new Carrier("DHL", "DHL Parcel"));
    }
}
