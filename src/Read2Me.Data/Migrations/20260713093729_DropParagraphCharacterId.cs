using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropParagraphCharacterId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Paragraphs_Characters_CharacterId",
                table: "Paragraphs");

            migrationBuilder.DropIndex(
                name: "IX_Paragraphs_CharacterId",
                table: "Paragraphs");

            migrationBuilder.DropColumn(
                name: "CharacterId",
                table: "Paragraphs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CharacterId",
                table: "Paragraphs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Paragraphs_CharacterId",
                table: "Paragraphs",
                column: "CharacterId");

            migrationBuilder.AddForeignKey(
                name: "FK_Paragraphs_Characters_CharacterId",
                table: "Paragraphs",
                column: "CharacterId",
                principalTable: "Characters",
                principalColumn: "Id");
        }
    }
}
