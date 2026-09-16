using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Orders;

namespace OrderService.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(order => order.Id);

        builder.Property(order => order.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(order => order.UserId).HasColumnName("user_id")
            .HasMaxLength(36).IsRequired();
        builder.Property(order => order.Currency).HasColumnName("currency")
            .HasMaxLength(3).IsRequired();
        builder.Property(order => order.StatusReason).HasColumnName("status_reason")
            .HasMaxLength(500);
        builder.Property(order => order.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(order => order.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Stored as an integer rather than a string: the state machine owns the meaning,
        // and a renamed enum member must not silently orphan existing rows.
        builder.Property(order => order.Status).HasColumnName("status")
            .HasConversion<int>().IsRequired();

        // The concurrency token maps onto Postgres's own xmin system column, so it is
        // configured by OrderDbContext only when the provider is Npgsql. Tests run on
        // SQLite, which has no such column.
        builder.HasIndex(order => order.UserId).HasDatabaseName("ix_orders_user_id");
        builder.HasIndex(order => order.Status).HasDatabaseName("ix_orders_status");

        // Lines have no meaning outside their order, so they load and cascade with it.
        builder.HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(order => order.Lines)
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();

        builder.Ignore(order => order.TotalAmount);
        builder.Ignore(order => order.TotalItems);
        builder.Ignore(order => order.IsTerminal);
    }
}
