using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Orders;

namespace OrderService.Infrastructure.Persistence.Configurations;

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines");
        builder.HasKey(line => line.Id);

        builder.Property(line => line.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(line => line.OrderId).HasColumnName("order_id").IsRequired();
        builder.Property(line => line.ProductId).HasColumnName("product_id")
            .HasMaxLength(36).IsRequired();
        builder.Property(line => line.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        builder.Property(line => line.ProductName).HasColumnName("product_name")
            .HasMaxLength(200).IsRequired();

        // Same precision as the Product Service. Money is never a floating point number.
        builder.Property(line => line.UnitPrice).HasColumnName("unit_price")
            .HasPrecision(12, 2).IsRequired();
        builder.Property(line => line.Currency).HasColumnName("currency")
            .HasMaxLength(3).IsRequired();
        builder.Property(line => line.Quantity).HasColumnName("quantity").IsRequired();

        builder.HasIndex(line => line.OrderId).HasDatabaseName("ix_order_lines_order_id");

        builder.Ignore(line => line.LineTotal);
    }
}
