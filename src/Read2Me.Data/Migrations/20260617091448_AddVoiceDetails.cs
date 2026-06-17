using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Title",
                table: "Voices",
                newName: "Name");

            migrationBuilder.AddColumn<string>(
                name: "AudioFileName",
                table: "Voices",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedUtc",
                table: "Voices",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Voices",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DesignPrompt",
                table: "Voices",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "Voices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Voices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                table: "Voices",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioFileName",
                table: "Voices");

            migrationBuilder.DropColumn(
                name: "CreatedUtc",
                table: "Voices");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Voices");

            migrationBuilder.DropColumn(
                name: "DesignPrompt",
                table: "Voices");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "Voices");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Voices");

            migrationBuilder.DropColumn(
                name: "Transcript",
                table: "Voices");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "Voices",
                newName: "Title");
        }
    }
}
