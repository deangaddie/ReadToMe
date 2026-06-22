using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceRules : Migration
    {
        // OrderHelper.GetBefore(null) == OrderKeyGenerator.GenerateKeyBetween(null, null) == "a0"
        // Used as the floor Rank for default rules so they sort before all non-default rules.
        private const string FloorRank = "a0";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoiceRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CharacterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rank = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false, collation: "BINARY"),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    FromLevel = table.Column<string>(type: "TEXT", nullable: true),
                    FromNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToLevel = table.Column<string>(type: "TEXT", nullable: true),
                    ToNodeId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoiceRules_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VoiceRules_Voices_VoiceId",
                        column: x => x.VoiceId,
                        principalTable: "Voices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceRules_CharacterId",
                table: "VoiceRules",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_VoiceRules_VoiceId",
                table: "VoiceRules",
                column: "VoiceId");

            // Backfill: for every Voice previously flagged IsDefault=1, insert a default VoiceRule.
            // Rank "a0" = OrderHelper.GetBefore(null) — the floor key that sorts before all non-default rules.
            migrationBuilder.Sql($@"
                INSERT INTO VoiceRules (Id, CharacterId, VoiceId, Rank, IsDefault, FromLevel, FromNodeId, ToLevel, ToNodeId)
                SELECT lower(hex(randomblob(4))) || '-' ||
                       lower(hex(randomblob(2))) || '-' ||
                       '4' || substr(lower(hex(randomblob(2))),2) || '-' ||
                       substr('89ab', abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' ||
                       lower(hex(randomblob(6))),
                       CharacterId,
                       Id,
                       '{FloorRank}',
                       1,
                       NULL, NULL, NULL, NULL
                FROM Voices
                WHERE IsDefault = 1
            ");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "Voices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "Voices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Restore IsDefault from the default rule before dropping the table.
            migrationBuilder.Sql(@"
                UPDATE Voices
                SET IsDefault = 1
                WHERE Id IN (SELECT VoiceId FROM VoiceRules WHERE IsDefault = 1)
            ");

            migrationBuilder.DropTable(
                name: "VoiceRules");
        }
    }
}
