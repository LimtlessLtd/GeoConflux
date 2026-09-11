using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeverityModelOpinion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelSeverity",
                table: "observations",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ModelSeverityConfidence",
                table: "observations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelVersion",
                table: "observations",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelSeverity",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "ModelSeverityConfidence",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "ModelVersion",
                table: "observations");
        }
    }
}
