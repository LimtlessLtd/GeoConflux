using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class GeopoliticsDbContext(DbContextOptions<GeopoliticsDbContext> options) : DbContext(options)
{
    public DbSet<GeopoliticalIncident> Incidents => Set<GeopoliticalIncident>();

    public DbSet<RawObservation> Observations => Set<RawObservation>();

    /// <summary>Append-only audit trail of enrichment attempts, successful and otherwise.</summary>
    public DbSet<AiInference> Inferences => Set<AiInference>();

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
        ConfigureInferences(modelBuilder, utcTicksConverter);
    }

    private static void ConfigureInferences(ModelBuilder modelBuilder, ValueConverter<DateTimeOffset, long> utcTicks)
    {
        var inference = modelBuilder.Entity<AiInference>();
        inference.ToTable("ai_inferences");
        inference.HasKey(value => value.Id);
        inference.Property(value => value.Provider).HasMaxLength(60).IsRequired();
        inference.Property(value => value.Model).HasMaxLength(120).IsRequired();
        inference.Property(value => value.PromptVersion).HasMaxLength(20).IsRequired();
        inference.Property(value => value.Outcome).HasConversion<string>().HasMaxLength(30).IsRequired();
        inference.Property(value => value.StructuredOutput).HasMaxLength(8000);
        inference.Property(value => value.Error).HasMaxLength(1000);
        inference.Property(value => value.CreatedAt).HasConversion(utcTicks).IsRequired();
        inference.Ignore(value => value.IsSuccess);

        // The two questions asked of this table are "how was this observation classified" and
        // "how is the provider behaving lately", so both get an index.
        inference.HasIndex(value => value.ObservationId);
        inference.HasIndex(value => new { value.Outcome, value.CreatedAt });

        // No foreign key to observations on purpose. An inference is evidence that an attempt
        // happened, and it must survive even if the observation it describes could not be stored.
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

        // Stored as a JSON array. Left unmapped, the collection comes back empty after a reload,
        // which quietly disables the domain's own guard against linking the same observation twice
        // and makes an incident's evidence list lie about itself.
        incident.PrimitiveCollection(value => value.ObservationIds)
            .HasColumnName("observation_ids")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

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
        observation.Property(value => value.ClassificationConfidence).IsRequired();
        observation.Property(value => value.ClassificationMethod).HasMaxLength(60).IsRequired();
        observation.Property(value => value.DetectedLanguage).HasMaxLength(16);
        observation.Property(value => value.SeverityRationale).HasMaxLength(AiInference.MaxRationaleLength);

        // Extracted entities live in a JSON column rather than a child table. They are read as a
        // set with their observation and never queried independently, so a join table would add a
        // table and a migration for no query the application actually issues.
        observation.OwnsMany(
            value => value.Entities,
            entity =>
            {
                entity.ToJson("entities");
                entity.Property(value => value.Name).HasMaxLength(ExtractedEntity.MaxNameLength).IsRequired();
                entity.Property(value => value.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
                entity.Ignore(value => value.MatchKey);
            });
        observation.Navigation(value => value.Entities).UsePropertyAccessMode(PropertyAccessMode.Field);

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
