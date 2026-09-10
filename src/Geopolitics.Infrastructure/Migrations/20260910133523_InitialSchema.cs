using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    location_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    location_country_code = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    latitude = table.Column<double>(type: "REAL", nullable: true),
                    longitude = table.Column<double>(type: "REAL", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservationCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    SourceIdentifier = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LocationResolutionNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LocationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    location_resolved_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    location_country_code = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    latitude = table.Column<double>(type: "REAL", nullable: true),
                    longitude = table.Column<double>(type: "REAL", nullable: true),
                    IncidentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DuplicateOfObservationId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_EventType_OccurredAt",
                table: "incidents",
                columns: new[] { "EventType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_OccurredAt",
                table: "incidents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Severity_OccurredAt",
                table: "incidents",
                columns: new[] { "Severity", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_observations_Fingerprint",
                table: "observations",
                column: "Fingerprint",
                unique: true,
                filter: "Status <> 'Duplicate'");

            migrationBuilder.CreateIndex(
                name: "IX_observations_IncidentId",
                table: "observations",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_observations_ReceivedAt",
                table: "observations",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "observations");
        }
    }
}
