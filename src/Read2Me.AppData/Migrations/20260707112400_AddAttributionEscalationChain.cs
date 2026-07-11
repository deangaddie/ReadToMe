using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributionEscalationChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AttributionSelfConsistency",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EscalationConfigIdsJson",
                table: "Settings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttributionSelfConsistency",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "EscalationConfigIdsJson",
                table: "Settings");
        }
    }
}
