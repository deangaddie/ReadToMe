using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmServerConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveLlmConfigId",
                table: "Settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LlmServerConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ApiType = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Temperature = table.Column<double>(type: "REAL", nullable: true),
                    TopP = table.Column<double>(type: "REAL", nullable: true),
                    MaxTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    FrequencyPenalty = table.Column<double>(type: "REAL", nullable: true),
                    PresencePenalty = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmServerConfigs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmServerConfigs");

            migrationBuilder.DropColumn(
                name: "ActiveLlmConfigId",
                table: "Settings");
        }
    }
}
