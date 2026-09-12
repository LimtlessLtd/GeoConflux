using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CollectedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CollectedAt",
                table: "observations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectedAt",
                table: "observations");
        }
    }
}
