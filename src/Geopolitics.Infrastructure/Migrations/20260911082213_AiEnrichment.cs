using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ClassificationConfidence",
                table: "observations",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationMethod",
                table: "observations",
                type: "TEXT",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DetectedLanguage",
                table: "observations",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeverityRationale",
                table: "observations",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "entities",
                table: "observations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClassificationConfidence",
                table: "incidents",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationMethod",
                table: "incidents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "observation_ids",
                table: "incidents",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "ai_inferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PromptVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LatencyMilliseconds = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StructuredOutput = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_inferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_inferences_ObservationId",
                table: "ai_inferences",
                column: "ObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_inferences_Outcome_CreatedAt",
                table: "ai_inferences",
                columns: new[] { "Outcome", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_inferences");

            migrationBuilder.DropColumn(
                name: "ClassificationConfidence",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "ClassificationMethod",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "DetectedLanguage",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "SeverityRationale",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "entities",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "ClassificationConfidence",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "ClassificationMethod",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "observation_ids",
                table: "incidents");
        }
    }
}
