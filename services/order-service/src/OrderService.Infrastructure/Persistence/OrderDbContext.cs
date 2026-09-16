using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrderService.Domain.Orders;

namespace OrderService.Infrastructure.Persistence;

/// <summary>
/// The Order Service's own database. Nothing outside this service connects to it.
/// </summary>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderDbContext).Assembly);

        if (Database.IsNpgsql())
        {
            // xmin is Postgres's own row version, so optimistic locking costs no extra
            // column. It is provider-specific, hence the guard: the test database is
            // SQLite and has nothing equivalent.
            modelBuilder.Entity<Order>()
                .Property(order => order.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }
        else
        {
            modelBuilder.Entity<Order>().Ignore(order => order.Version);
            ApplyNonRelationalDateTimeOffsetConverter(modelBuilder);
        }

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Stores <see cref="DateTimeOffset"/> as UTC ticks on providers that cannot sort it.
    /// </summary>
    /// <remarks>
    /// SQLite — which only the test suite uses — refuses to put a DateTimeOffset in an
    /// ORDER BY. Postgres has no such limitation, so production keeps native timestamptz
    /// columns and this converter never applies there. Ticks are a long, so ordering is
    /// preserved exactly. The values written are always UTC, so no offset is lost.
    ///
    /// The cleaner long-term answer is to run these tests against a real Postgres via
    /// Testcontainers; that needs Docker, which this project does not yet require.
    /// </remarks>
    private static void ApplyNonRelationalDateTimeOffsetConverter(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties()
                         .Where(property => property.ClrType == typeof(DateTimeOffset)))
            {
                property.SetValueConverter(converter);
            }
        }
    }
}
