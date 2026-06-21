using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddSentenceChunkingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SentenceMinChunkChars",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "SentencePauseMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<bool>(
                name: "SentenceSplitEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SentenceMinChunkChars",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SentencePauseMs",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SentenceSplitEnabled",
                table: "Settings");
        }
    }
}
