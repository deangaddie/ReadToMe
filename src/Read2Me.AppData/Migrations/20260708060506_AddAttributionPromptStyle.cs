using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributionPromptStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SimpleBatchCharacterPrompt",
                table: "PromptSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimpleCharacterPrompt",
                table: "PromptSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptStyle",
                table: "LlmServerConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SimpleBatchCharacterPrompt",
                table: "PromptSettings");

            migrationBuilder.DropColumn(
                name: "SimpleCharacterPrompt",
                table: "PromptSettings");

            migrationBuilder.DropColumn(
                name: "PromptStyle",
                table: "LlmServerConfigs");
        }
    }
}
