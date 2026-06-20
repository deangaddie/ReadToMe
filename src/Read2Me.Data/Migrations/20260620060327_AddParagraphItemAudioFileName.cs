using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParagraphItemAudioFileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioFileName",
                table: "ParagraphItems",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioFileName",
                table: "ParagraphItems");
        }
    }
}
