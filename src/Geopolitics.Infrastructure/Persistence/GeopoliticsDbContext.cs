using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class GeopoliticsDbContext(DbContextOptions<GeopoliticsDbContext> options) : DbContext(options)
{
    public DbSet<GeopoliticalIncident> Incidents => Set<GeopoliticalIncident>();

    public DbSet<RawObservation> Observations => Set<RawObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite has no native offset-aware timestamp, and storing text would make range queries
        // depend on formatting. UTC ticks sort correctly and round-trip exactly.
        var utcTicksConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcDateTime.Ticks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        var nullableUtcTicksConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value == null ? null : value.Value.UtcDateTime.Ticks,
            value => value == null ? null : new DateTimeOffset(value.Value, TimeSpan.Zero));

        ConfigureIncidents(modelBuilder, utcTicksConverter);
        ConfigureObservations(modelBuilder, utcTicksConverter, nullableUtcTicksConverter);
    }

    private static void ConfigureIncidents(ModelBuilder modelBuilder, ValueConverter<DateTimeOffset, long> utcTicks)
    {
        var incident = modelBuilder.Entity<GeopoliticalIncident>();
        incident.ToTable("incidents");
        incident.HasKey(value => value.Id);
        incident.Property(value => value.Title).HasMaxLength(300).IsRequired();
        incident.Property(value => value.Summary).HasMaxLength(4000).IsRequired();
        incident.Property(value => value.EventType).HasConversion<string>().HasMaxLength(40).IsRequired();
        incident.Property(value => value.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        incident.Property(value => value.OccurredAt).HasConversion(utcTicks).IsRequired();
        incident.Property(value => value.CreatedAt).HasConversion(utcTicks).IsRequired();
        incident.Property(value => value.UpdatedAt).HasConversion(utcTicks).IsRequired();
        incident.HasIndex(value => value.OccurredAt);
        incident.HasIndex(value => new { value.Severity, value.OccurredAt });

        // The correlator's pre-filter is (EventType, OccurredAt range); this index serves it directly.
        incident.HasIndex(value => new { value.EventType, value.OccurredAt });

        incident.OwnsOne(
            value => value.Location,
            location =>
            {
                location.Property(value => value.Name).HasColumnName("location_name").HasMaxLength(200);
                location.Property(value => value.CountryCode).HasColumnName("location_country_code").HasMaxLength(8);
                location.Property(value => value.Latitude).HasColumnName("latitude");
                location.Property(value => value.Longitude).HasColumnName("longitude");
            });
    }

    private static void ConfigureObservations(
        ModelBuilder modelBuilder,
        ValueConverter<DateTimeOffset, long> utcTicks,
        ValueConverter<DateTimeOffset?, long?> nullableUtcTicks)
    {
        var observation = modelBuilder.Entity<RawObservation>();
        observation.ToTable("observations");
        observation.HasKey(value => value.Id);
        observation.Property(value => value.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        observation.Property(value => value.SourceName).HasMaxLength(200).IsRequired();
        observation.Property(value => value.Content).HasMaxLength(20_000).IsRequired();
        observation.Property(value => value.SourceIdentifier).HasMaxLength(400);
        observation.Property(value => value.Fingerprint).HasMaxLength(64).IsRequired();
        observation.Property(value => value.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        observation.Property(value => value.FailureReason).HasMaxLength(1000);
        observation.Property(value => value.LocationResolutionNote).HasMaxLength(500);
        observation.Property(value => value.Title).HasMaxLength(300);
        observation.Property(value => value.Summary).HasMaxLength(4000);
        observation.Property(value => value.EventType).HasConversion<string>().HasMaxLength(40).IsRequired();
        observation.Property(value => value.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        observation.Property(value => value.LocationName).HasMaxLength(200);
        observation.Property(value => value.ReceivedAt).HasConversion(utcTicks).IsRequired();
        observation.Property(value => value.OccurredAt).HasConversion(nullableUtcTicks);

        // Deduplication depends on this constraint rather than on the read-then-insert check alone,
        // because concurrent processors can both pass that check for the same payload.
        observation.HasIndex(value => value.Fingerprint)
            .HasFilter("Status <> 'Duplicate'")
            .IsUnique();

        observation.HasIndex(value => value.ReceivedAt);
        observation.HasIndex(value => value.IncidentId);

        observation.OwnsOne(
            value => value.Location,
            location =>
            {
                location.Property(value => value.Name).HasColumnName("location_resolved_name").HasMaxLength(200);
                location.Property(value => value.CountryCode).HasColumnName("location_country_code").HasMaxLength(8);
                location.Property(value => value.Latitude).HasColumnName("latitude");
                location.Property(value => value.Longitude).HasColumnName("longitude");
            });
    }
}
