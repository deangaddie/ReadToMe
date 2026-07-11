using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoverCharactersPrompt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscoverCharactersPrompt",
                table: "PromptSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoverCharactersPrompt",
                table: "PromptSettings");
        }
    }
}
