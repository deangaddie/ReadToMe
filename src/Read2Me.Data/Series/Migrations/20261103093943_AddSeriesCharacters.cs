using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Series.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesCharacters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeriesCharacters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesCharacters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Links",
                columns: table => new
                {
                    SeriesCharacterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CharacterId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Links", x => new { x.SeriesCharacterId, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_Links_SeriesCharacters_SeriesCharacterId",
                        column: x => x.SeriesCharacterId,
                        principalTable: "SeriesCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Links_ProjectId_CharacterId",
                table: "Links",
                columns: new[] { "ProjectId", "CharacterId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Links");

            migrationBuilder.DropTable(
                name: "SeriesCharacters");
        }
    }
}
