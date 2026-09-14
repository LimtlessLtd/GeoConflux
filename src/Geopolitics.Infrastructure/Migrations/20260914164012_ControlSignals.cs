using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ControlSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "control_actor",
                table: "observations",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "control_basis",
                table: "observations",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            // "None" rather than the empty string EF generates by default. The column stores the
            // enum by name, and "" is not one of its members - so every row that existed before this
            // migration would fail to materialise, which is a corrupt read rather than a wrong value.
            migrationBuilder.AddColumn<string>(
                name: "control_signal",
                table: "observations",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateIndex(
                name: "IX_observations_control_signal_OccurredAt",
                table: "observations",
                columns: new[] { "control_signal", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_observations_control_signal_OccurredAt",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "control_actor",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "control_basis",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "control_signal",
                table: "observations");
        }
    }
}
