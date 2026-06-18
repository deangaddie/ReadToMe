using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class ExpandAppTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppbarBackground",
                table: "Themes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Background",
                table: "Themes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrawerBackground",
                table: "Themes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Surface",
                table: "Themes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextPrimary",
                table: "Themes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextSecondary",
                table: "Themes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppbarBackground",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "Background",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "DrawerBackground",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "Surface",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "TextPrimary",
                table: "Themes");

            migrationBuilder.DropColumn(
                name: "TextSecondary",
                table: "Themes");
        }
    }
}
