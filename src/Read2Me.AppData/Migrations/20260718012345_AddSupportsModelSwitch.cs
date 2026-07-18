using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportsModelSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsModelSwitch",
                table: "LlmServerConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Backfill: existing configs whose BaseUrl resolves via DockerAiServiceRegistry to the
            // llama endpoint (http://localhost:8080) can switch models on demand. The registry
            // normalises host (127.0.0.1 <-> localhost) and tolerates a trailing slash, so mirror
            // that here: lowercase and strip a trailing '/' before matching the two host forms.
            migrationBuilder.Sql(
                "UPDATE LlmServerConfigs SET SupportsModelSwitch = 1 " +
                "WHERE lower(rtrim(BaseUrl, '/')) IN ('http://localhost:8080', 'http://127.0.0.1:8080');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportsModelSwitch",
                table: "LlmServerConfigs");
        }
    }
}
