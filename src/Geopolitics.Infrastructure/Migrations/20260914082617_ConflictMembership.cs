using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConflictMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conflict_basis",
                table: "observations",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "conflict_candidate_keys",
                table: "observations",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "conflict_keys",
                table: "observations",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "conflict_note",
                table: "observations",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conflict_basis",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "conflict_candidate_keys",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "conflict_keys",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "conflict_note",
                table: "observations");
        }
    }
}
