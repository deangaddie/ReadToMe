using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Series.Migrations
{
    /// <inheritdoc />
    public partial class AddNotTheSame : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotTheSame",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CharacterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesCharacterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OtherProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OtherCharacterId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotTheSame", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotTheSame_SeriesCharacters_SeriesCharacterId",
                        column: x => x.SeriesCharacterId,
                        principalTable: "SeriesCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotTheSame_ProjectId_CharacterId",
                table: "NotTheSame",
                columns: new[] { "ProjectId", "CharacterId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotTheSame_SeriesCharacterId",
                table: "NotTheSame",
                column: "SeriesCharacterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotTheSame");
        }
    }
}
