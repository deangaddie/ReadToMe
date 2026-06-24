using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddToSentenceCaseConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToSentenceCaseConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParagraphTtsServiceConfigId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParagraphEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    WordEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    WordMinLength = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToSentenceCaseConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToSentenceCaseConfigs_ParagraphTtsServiceConfigs_ParagraphTtsServiceConfigId",
                        column: x => x.ParagraphTtsServiceConfigId,
                        principalTable: "ParagraphTtsServiceConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToSentenceCaseConfigs_ParagraphTtsServiceConfigId",
                table: "ToSentenceCaseConfigs",
                column: "ParagraphTtsServiceConfigId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToSentenceCaseConfigs");
        }
    }
}
