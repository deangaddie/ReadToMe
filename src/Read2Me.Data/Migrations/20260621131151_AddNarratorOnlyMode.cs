using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNarratorOnlyMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NarratorOnlyMode",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NarratorOnlyMode",
                table: "Projects");
        }
    }
}
