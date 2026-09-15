using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geopolitics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ObservationTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranslatedSummary",
                table: "observations",
                type: "TEXT",
                maxLength: 1200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranslatedTitle",
                table: "observations",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Translation",
                table: "observations",
                type: "TEXT",
                maxLength: 20,
                nullable: false,

                // "NotTranslated", not the empty string the scaffolder proposes. Every row written
                // before this migration was written by a pipeline that did not translate, so this is
                // the true value for all of them -- and an empty string is not a member of the enum
                // at all, so it would fail to convert the moment one of those rows was read back.
                defaultValue: "NotTranslated");

            migrationBuilder.AddColumn<string>(
                name: "TranslationMethod",
                table: "observations",
                type: "TEXT",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranslatedSummary",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "TranslatedTitle",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "Translation",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "TranslationMethod",
                table: "observations");
        }
    }
}
