using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class RenameChunkPauseAndDropMinChunk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SentenceMinChunkChars",
                table: "Settings");

            migrationBuilder.RenameColumn(
                name: "SentencePauseMs",
                table: "Settings",
                newName: "ChunkPauseMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ChunkPauseMs",
                table: "Settings",
                newName: "SentencePauseMs");

            migrationBuilder.AddColumn<int>(
                name: "SentenceMinChunkChars",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);
        }
    }
}
