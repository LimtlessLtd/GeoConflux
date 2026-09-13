using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SourceAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "observations",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclaredLanguage",
                table: "observations",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "observations",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            // Not the scaffolded empty string, which is not a SourceTier name and would make every
            // pre-existing row fail to materialise. Rows written before this column existed came
            // from wires, datasets and instruments, so Published is both the safe default and the
            // true one — and defaulting the other way would retroactively gate published reporting
            // behind a corroboration rule it was never subject to.
            migrationBuilder.AddColumn<string>(
                name: "Tier",
                table: "observations",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Published");

            migrationBuilder.CreateIndex(
                name: "IX_observations_Tier_Status_OccurredAt",
                table: "observations",
                columns: new[] { "Tier", "Status", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_observations_Tier_Status_OccurredAt",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "DeclaredLanguage",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "observations");
        }
    }
}
