using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNarratorCharacterId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NarratorCharacterId",
                table: "Projects",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NarratorCharacterId",
                table: "Projects");
        }
    }
}
