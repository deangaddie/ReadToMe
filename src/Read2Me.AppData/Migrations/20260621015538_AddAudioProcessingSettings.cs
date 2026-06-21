using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioProcessingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FfmpegPath",
                table: "Settings",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WerThreshold",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.14999999999999999);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FfmpegPath",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WerThreshold",
                table: "Settings");
        }
    }
}
