using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DowntimeLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "UpdatedAt",
                table: "IngestionCheckpoints",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<long>(
                name: "RequestedFrom",
                table: "IngestionCheckpoints",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<long>(
                name: "LastPolledAt",
                table: "IngestionCheckpoints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PollEvery",
                table: "IngestionCheckpoints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DowntimePeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DetectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSource = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Cadence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DowntimePeriods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DowntimePeriods_StartedAt",
                table: "DowntimePeriods",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DowntimePeriods");

            migrationBuilder.DropColumn(
                name: "LastPolledAt",
                table: "IngestionCheckpoints");

            migrationBuilder.DropColumn(
                name: "PollEvery",
                table: "IngestionCheckpoints");

            migrationBuilder.AlterColumn<long>(
                name: "UpdatedAt",
                table: "IngestionCheckpoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "RequestedFrom",
                table: "IngestionCheckpoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
