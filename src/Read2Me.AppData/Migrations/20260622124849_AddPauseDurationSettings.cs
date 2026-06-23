using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddPauseDurationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChapterPauseMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2500);

            migrationBuilder.AddColumn<int>(
                name: "ParagraphPauseMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 800);

            migrationBuilder.AddColumn<int>(
                name: "PartPauseMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3000);

            migrationBuilder.AddColumn<int>(
                name: "PauseMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 500);

            migrationBuilder.AddColumn<int>(
                name: "VolumePauseMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChapterPauseMs",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ParagraphPauseMs",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PartPauseMs",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PauseMs",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "VolumePauseMs",
                table: "Settings");
        }
    }
}
