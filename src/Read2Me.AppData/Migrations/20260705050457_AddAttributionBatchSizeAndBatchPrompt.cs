using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributionBatchSizeAndBatchPrompt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BatchCharacterPrompt",
                table: "PromptSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttributionBatchSize",
                table: "LlmServerConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatchCharacterPrompt",
                table: "PromptSettings");

            migrationBuilder.DropColumn(
                name: "AttributionBatchSize",
                table: "LlmServerConfigs");
        }
    }
}
