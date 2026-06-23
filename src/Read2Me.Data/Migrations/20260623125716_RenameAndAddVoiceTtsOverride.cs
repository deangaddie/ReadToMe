using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameAndAddVoiceTtsOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SettingsOverrideJson",
                table: "Voices",
                newName: "VoiceDesignSettingsOverrideJson");

            migrationBuilder.AddColumn<string>(
                name: "TtsSettingsOverrideJson",
                table: "Voices",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TtsSettingsOverrideJson",
                table: "Voices");

            migrationBuilder.RenameColumn(
                name: "VoiceDesignSettingsOverrideJson",
                table: "Voices",
                newName: "SettingsOverrideJson");
        }
    }
}
