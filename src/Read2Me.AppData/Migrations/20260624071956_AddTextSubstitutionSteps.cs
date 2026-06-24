using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class AddTextSubstitutionSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TextSubstitutionSteps",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ParagraphTtsServiceConfigId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromText = table.Column<string>(type: "TEXT", nullable: false),
                    ToText = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextSubstitutionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TextSubstitutionSteps_ParagraphTtsServiceConfigs_ParagraphTtsServiceConfigId",
                        column: x => x.ParagraphTtsServiceConfigId,
                        principalTable: "ParagraphTtsServiceConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TextSubstitutionSteps_ParagraphTtsServiceConfigId_Order",
                table: "TextSubstitutionSteps",
                columns: new[] { "ParagraphTtsServiceConfigId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TextSubstitutionSteps");
        }
    }
}
