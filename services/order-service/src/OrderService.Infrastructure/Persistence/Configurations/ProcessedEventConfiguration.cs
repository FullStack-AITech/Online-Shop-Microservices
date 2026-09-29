using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OrderService.Infrastructure.Persistence.Configurations;

/// <summary>
/// The shared deduplication table from <c>docs/events/README.md</c>, column for column.
/// </summary>
public sealed class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("processed_events");
        builder.HasKey(processed => new { processed.EventId, processed.Consumer });

        builder.Property(processed => processed.EventId).HasColumnName("event_id")
            .ValueGeneratedNever();
        builder.Property(processed => processed.Consumer).HasColumnName("consumer")
            .HasMaxLength(64).IsRequired();

        // The now() default is Postgres syntax, so OrderDbContext adds it only on Npgsql.
        // The application always sets the value itself anyway.
        builder.Property(processed => processed.ProcessedAt).HasColumnName("processed_at")
            .IsRequired();
    }
}
