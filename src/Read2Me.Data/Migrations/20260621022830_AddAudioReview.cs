using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudioReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParagraphItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizeOk = table.Column<bool>(type: "INTEGER", nullable: false),
                    NormalizeReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    VerifyOk = table.Column<bool>(type: "INTEGER", nullable: false),
                    Wer = table.Column<double>(type: "REAL", nullable: true),
                    VerifyReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Transcript = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    OriginalTextSnapshot = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioReviews_ParagraphItems_ParagraphItemId",
                        column: x => x.ParagraphItemId,
                        principalTable: "ParagraphItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudioReviews_ParagraphItemId",
                table: "AudioReviews",
                column: "ParagraphItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioReviews");
        }
    }
}
