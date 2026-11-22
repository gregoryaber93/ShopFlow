using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopFlow.Inventory.Domain.Catalog;

namespace ShopFlow.Inventory.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", "catalog");
        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id).HasColumnName("Sku").HasMaxLength(64).IsUnicode(false);

        builder.Ignore(product => product.Sku);
        builder.Ignore(product => product.Price);
        builder.Ignore(product => product.DomainEvents);
        builder.Ignore(product => product.ConcurrencyToken);

        builder.Property(product => product.Name).HasMaxLength(200).IsRequired();
        builder.Property(product => product.Description).HasMaxLength(2000);

        // EF Core writes and reads the entity's private fields; the domain exposes a Money value built from them.
        builder.Property<decimal>("_priceAmount").HasColumnName("PriceAmount");
        builder.Property<string>("_priceCurrency").HasColumnName("PriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);

        // AZURE/SQL: rowversion is an 8-byte counter changed by SQL Server on every UPDATE of the row.
        // Passed to the client as an ETag (Base64), it gives the same optimistic concurrency as _etag in Cosmos DB.
        builder.Property<byte[]>(InventoryDbContext.RowVersion).IsRowVersion();

        builder.HasOne<Category>().WithMany().HasForeignKey(product => product.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(product => new { product.CategoryId, product.IsActive });

        // Products are not part of the migrations: the catalog reaches the database through an import from the PIM system, not a deployment.
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories", "catalog");
        builder.HasKey(category => category.Id);
        builder.Property(category => category.Id).ValueGeneratedNever();
        builder.Property(category => category.Name).HasMaxLength(100).IsRequired();
        builder.HasOne<Category>().WithMany().HasForeignKey(category => category.ParentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            new Category(1, "Dom", null),
            new Category(2, "Kuchnia", 1),
            new Category(3, "Elektronika", null));
    }
}
