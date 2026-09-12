using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>Controls the illustrative records written when the database is empty.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>
    /// Whether to write illustrative incidents into an empty database. They exist so a first run has
    /// something on the globe before the pipeline produces anything. Turn this off when the database
    /// is meant to contain only genuine pipeline output, such as when exporting a published snapshot.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed class DemoDataSeeder(GeopoliticsDbContext dbContext, IOptions<SeedOptions> options)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        if (await dbContext.Incidents.AnyAsync(cancellationToken))
        {
            return;
        }

        var seededAt = DateTimeOffset.UtcNow;
        var incidents = new[]
        {
            new GeopoliticalIncident(
                Guid.Parse("a63a2a54-4dc8-4ec6-9947-d1edb52949fd"),
                "Demo maritime safety report near Bab-el-Mandeb",
                "Illustrative local seed record used to demonstrate the dashboard; it is not live reporting.",
                EventType.MaritimeIncident,
                Severity.High,
                seededAt.AddHours(-2),
                new GeoLocation("Bab-el-Mandeb", "DJ", 12.585, 43.334),
                ObservationProvenance.Recorded,
                seededAt),
            new GeopoliticalIncident(
                Guid.Parse("0a66cd6c-078e-463e-9a2c-21f4b6d6f27f"),
                "Demo disruption scenario in the Black Sea",
                "Illustrative local seed record used to demonstrate severity and location rendering; it is not live reporting.",
                EventType.NavalIncident,
                Severity.Medium,
                seededAt.AddHours(-7),
                new GeoLocation("Black Sea", null, 43.0, 34.0),
                ObservationProvenance.Recorded,
                seededAt),
            new GeopoliticalIncident(
                Guid.Parse("b8fa1513-b1a6-4dcd-ac42-9bbb46bf84d3"),
                "Demo civil protection scenario in the Levant",
                "Illustrative local seed record used to demonstrate the intelligence shell; it is not a verified external event.",
                EventType.NaturalHazard,
                Severity.Low,
                seededAt.AddHours(-18),
                new GeoLocation("Eastern Mediterranean", null, 34.7, 35.9),
                ObservationProvenance.Recorded,
                seededAt),
        };

        await dbContext.Incidents.AddRangeAsync(incidents, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
