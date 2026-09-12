using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <summary>
    /// Replaces the demo/live boolean with a three-valued provenance.
    /// <para>
    /// Hand-written rather than left as scaffolded. The generated version dropped <c>IsDemo</c> before
    /// anything read it and defaulted the new column to the empty string, which is not the name of any
    /// member of the enum — so every existing row would have been unreadable and the one fact worth
    /// preserving would have been discarded on the way. The order here is add, backfill, then drop.
    /// </para>
    /// </summary>
    public partial class ObservationProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Recorded is the safe default for a row written before this column existed: understating
            // a real report costs visibility, where the reverse would present synthetic data as fact.
            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "observations",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Recorded");

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "incidents",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Recorded");

            // Everything that was not demo data arrived by a polling adapter or a manual submission.
            // Neither was collected, and nothing existing could have been, because no bundle had ever
            // been read at the point this migration runs.
            migrationBuilder.Sql(
                "UPDATE observations SET Provenance = CASE WHEN IsDemo = 1 THEN 'Recorded' ELSE 'Polled' END;");

            migrationBuilder.Sql(
                "UPDATE incidents SET Provenance = CASE WHEN IsDemo = 1 THEN 'Recorded' ELSE 'Polled' END;");

            migrationBuilder.DropColumn(name: "IsDemo", table: "observations");
            migrationBuilder.DropColumn(name: "IsDemo", table: "incidents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "observations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "incidents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Collected collapses into "not demo", which is the closest the old column could express.
            // The distinction this migration added is genuinely lost on the way down, and saying so
            // here is better than implying the round trip is lossless.
            migrationBuilder.Sql("UPDATE observations SET IsDemo = CASE WHEN Provenance = 'Recorded' THEN 1 ELSE 0 END;");
            migrationBuilder.Sql("UPDATE incidents SET IsDemo = CASE WHEN Provenance = 'Recorded' THEN 1 ELSE 0 END;");

            migrationBuilder.DropColumn(name: "Provenance", table: "observations");
            migrationBuilder.DropColumn(name: "Provenance", table: "incidents");
        }
    }
}
