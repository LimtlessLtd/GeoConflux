using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class GeopoliticsDbContext(DbContextOptions<GeopoliticsDbContext> options) : DbContext(options)
{
    /// <summary>SQLite extended result code for a unique-constraint violation.</summary>
    private const int SqliteConstraintUnique = 2067;

    public DbSet<GeopoliticalIncident> Incidents => Set<GeopoliticalIncident>();

    public DbSet<RawObservation> Observations => Set<RawObservation>();

    /// <summary>Append-only audit trail of enrichment attempts, successful and otherwise.</summary>
    public DbSet<AiInference> Inferences => Set<AiInference>();

    /// <summary>
    /// How far back each dataset adapter has asked, so a backfill survives a restart, and when each
    /// one last polled, which is the only evidence this host has of having been running.
    /// </summary>
    public DbSet<IngestionCheckpoint> Checkpoints => Set<IngestionCheckpoint>();

    /// <summary>Periods this host was not running, worked out at startup and kept for Sprint 18.</summary>
    public DbSet<DowntimePeriod> Downtime => Set<DowntimePeriod>();

    /// <summary>
    /// Commits, translating a lost deduplication race into a signal the pipeline understands.
    /// <para>
    /// This lives on the context rather than in a repository because it is a property of the
    /// database, not of whoever happened to call save. The observation and the incident commit
    /// together in one unit of work, so the save that inserts an observation is frequently issued
    /// through the incident repository — and when the translation lived in only one of the two, a
    /// concurrent redelivery surfaced as an unhandled provider exception, was counted as a pipeline
    /// failure, and cost the very evidence the duplicate path exists to retain.
    /// </para>
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsFingerprintConflict(exception))
        {
            // The read-then-insert check cannot be atomic across concurrent processors, so the
            // filtered unique index is the real authority. Translate rather than let a provider
            // exception reach the Application layer.
            throw new DuplicateObservationException(FingerprintOf(exception), exception);
        }
    }

    private static bool IsFingerprintConflict(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique } inner
        && inner.Message.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase);

    private static string FingerprintOf(DbUpdateException exception) =>
        exception.Entries
            .Select(entry => entry.Entity)
            .OfType<RawObservation>()
            .Select(observation => observation.Fingerprint)
            .FirstOrDefault() ?? "unknown";

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
        ConfigureCheckpoints(modelBuilder, utcTicksConverter, nullableUtcTicksConverter);
        ConfigureDowntime(modelBuilder, utcTicksConverter);
    }

    /// <summary>
    /// One row per period the host was away. No unique constraint on the interval: two overlapping
    /// periods would be a fault worth seeing rather than one worth silently refusing, and a ledger
    /// that rejected a write would lose the very gap it exists to record.
    /// </summary>
    private static void ConfigureDowntime(ModelBuilder modelBuilder, ValueConverter<DateTimeOffset, long> utcTicks)
    {
        modelBuilder.Entity<DowntimePeriod>(period =>
        {
            period.ToTable("DowntimePeriods");
            period.HasKey(value => value.Id);
            period.Property(value => value.StartedAt).HasConversion(utcTicks).IsRequired();
            period.Property(value => value.EndedAt).HasConversion(utcTicks).IsRequired();
            period.Property(value => value.DetectedAt).HasConversion(utcTicks).IsRequired();
            period.Property(value => value.LastSource).HasMaxLength(60).IsRequired();

            // Stored as ticks for the same reason the timestamps are: a formatted interval would
            // make a comparison depend on how it was written.
            period.Property(value => value.Cadence)
                .HasConversion(value => value.Ticks, value => TimeSpan.FromTicks(value))
                .IsRequired();

            // The one question asked of this table is "what did we miss, most recently first".
            period.HasIndex(value => value.StartedAt);
        });
    }

    /// <summary>
    /// One row per adapter, keyed by its name. There is no surrogate key because there is nothing a
    /// surrogate would buy: a source has exactly one frontier, and letting the table hold two rows
    /// for "acled" would be letting it hold two answers to a question with one.
    /// </summary>
    private static void ConfigureCheckpoints(
        ModelBuilder modelBuilder,
        ValueConverter<DateTimeOffset, long> utcTicks,
        ValueConverter<DateTimeOffset?, long?> nullableUtcTicks)
    {
        modelBuilder.Entity<IngestionCheckpoint>(checkpoint =>
        {
            checkpoint.ToTable("IngestionCheckpoints");
            checkpoint.HasKey(value => value.Source);
            checkpoint.Property(value => value.Source).HasMaxLength(60);

            // Nullable, and the nullability is the point. A row now exists for any source that has
            // polled, which is most of them and almost none of which backfill. Defaulting the
            // frontier to a date would tell the backfill that history from that date had already
            // been requested, and the walk would start short of where it should and never notice.
            checkpoint.Property(value => value.RequestedFrom).HasConversion(nullableUtcTicks);
            checkpoint.Property(value => value.UpdatedAt).HasConversion(nullableUtcTicks);
            checkpoint.Property(value => value.LastPolledAt).HasConversion(nullableUtcTicks);
            checkpoint.Property(value => value.PollEvery)
                .HasConversion(value => value!.Value.Ticks, value => TimeSpan.FromTicks(value));
        });
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
        incident.Property(value => value.Provenance).HasConversion<string>().HasMaxLength(20).IsRequired();
        incident.Ignore(value => value.IsDemo);
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

        // Same treatment, and for the same reason: correlation reads these back on the hot path, and
        // an unmapped collection would return empty after a reload and silently score every
        // candidate as sharing no actors.
        incident.PrimitiveCollection(value => value.EntityKeys)
            .HasColumnName("entity_keys")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        incident.OwnsOne(
            value => value.Location,
            location =>
            {
                location.Property(value => value.Name).HasColumnName("location_name").HasMaxLength(200);
                location.Property(value => value.Precision).HasColumnName("location_precision").HasConversion<string>().HasMaxLength(20);
                location.Property(value => value.CountryCode).HasColumnName("location_country_code").HasMaxLength(8);
                location.Property(value => value.Latitude).HasColumnName("latitude");
                location.Property(value => value.Longitude).HasColumnName("longitude");

                // Serves the bounding-box pre-filter behind every spatial query. Latitude leads
                // because it is the selective half: a degree of latitude is a fixed distance, so the
                // latitude band alone eliminates most of the table before longitude is considered.
                location.HasIndex(value => new { value.Latitude, value.Longitude });
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
        observation.Property(value => value.Provenance).HasConversion<string>().HasMaxLength(20).IsRequired();
        observation.Property(value => value.CollectedAt).HasConversion(nullableUtcTicks);
        observation.Property(value => value.Tier).HasConversion<string>().HasMaxLength(20).IsRequired();
        observation.Property(value => value.Platform).HasMaxLength(60);
        observation.Property(value => value.Channel).HasMaxLength(200);
        observation.Property(value => value.DeclaredLanguage).HasMaxLength(16);

        // Reassembled from the three columns above rather than stored, for the same reason IsDemo is.
        observation.Ignore(value => value.Attribution);

        // The gate's read: every held claim in a time window. Tier leads because it is the selective
        // half — claims are a small minority of the table, so filtering on it first discards nearly
        // all of it before a status or a timestamp is considered.
        observation.HasIndex(value => new { value.Tier, value.Status, value.OccurredAt });

        // Derived from Provenance rather than stored. Two columns would eventually disagree.
        observation.Ignore(value => value.IsDemo);
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

        // Stored as its name, like every other enum here, so a row read straight out of the database
        // says "MachineTranslated" rather than "2". The English text sits beside the source text
        // rather than replacing it: a reader checking a translated claim needs the original, and the
        // original is the part that is actually evidence.
        observation.Property(value => value.Translation).HasConversion<string>().HasMaxLength(20).IsRequired();
        observation.Property(value => value.TranslatedTitle).HasMaxLength(RawObservation.MaxTranslatedTitleLength);
        observation.Property(value => value.TranslatedSummary).HasMaxLength(RawObservation.MaxTranslatedSummaryLength);
        observation.Property(value => value.TranslationMethod).HasMaxLength(60);
        observation.Ignore(value => value.HasEnglishText);

        // Nullable throughout, and meant to be. A null here is "no second opinion was recorded",
        // which is a different fact from any severity value the model could have chosen.
        observation.Property(value => value.ModelSeverity).HasConversion<string>().HasMaxLength(20);
        observation.Property(value => value.ModelVersion).HasMaxLength(120);
        observation.Ignore(value => value.ModelDisagrees);

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

        // Conflict membership, stored as JSON arrays for the same reason the entity keys on an
        // incident are: they are read with their row and never joined against. What they are not is
        // optional — an unmapped collection comes back empty after a reload, and a report that
        // belonged to a war would quietly stop belonging to it the moment it left memory.
        observation.PrimitiveCollection(value => value.ConflictKeys)
            .HasColumnName("conflict_keys")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        observation.PrimitiveCollection(value => value.ConflictCandidateKeys)
            .HasColumnName("conflict_candidate_keys")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        observation.Property(value => value.ConflictBasis)
            .HasColumnName("conflict_basis")
            .HasConversion<string>()
            .HasMaxLength(20);
        observation.Property(value => value.ConflictNote).HasColumnName("conflict_note").HasMaxLength(500);
        observation.Ignore(value => value.IsAssignedToConflict);

        // What a report says about who holds a place, as distinct from what happened there. Stored
        // as three columns rather than as a child table because they are read with their row and
        // never joined against, which is the same reasoning the conflict columns above rest on.
        observation.Property(value => value.ControlSignal)
            .HasColumnName("control_signal")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();
        observation.Property(value => value.ControlActor)
            .HasColumnName("control_actor")
            .HasMaxLength(RawObservation.MaxControlActorLength);
        observation.Property(value => value.ControlBasis)
            .HasColumnName("control_basis")
            .HasConversion<string>()
            .HasMaxLength(20);
        observation.Ignore(value => value.CarriesControlSignal);

        // The assessment's read: every report carrying control evidence, newest first. Signal leads
        // because it is overwhelmingly the selective half — almost every observation carries None,
        // so filtering on it first discards nearly the whole table.
        observation.HasIndex(value => new { value.ControlSignal, value.OccurredAt });

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
                location.Property(value => value.Precision).HasColumnName("location_precision").HasConversion<string>().HasMaxLength(20);
                location.Property(value => value.CountryCode).HasColumnName("location_country_code").HasMaxLength(8);
                location.Property(value => value.Latitude).HasColumnName("latitude");
                location.Property(value => value.Longitude).HasColumnName("longitude");
            });
    }
}
