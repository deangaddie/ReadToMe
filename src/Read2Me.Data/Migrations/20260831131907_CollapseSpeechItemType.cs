using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <summary>
    /// Collapses the item type's two speech kinds into one. Narration and dialog are told apart by
    /// the speaker now (ADR-0006), so 'Narration' and 'Character' both become 'Speech'; the five
    /// pause kinds are untouched, keeping their stored spelling.
    /// <para>
    /// Safe only after <c>BackfillNarrationSpeakers</c>, which is what guarantees every row that
    /// used to say 'Narration' carries the narrator — once the word is gone, nothing can recover
    /// which rows it named. The type is stored as text, so this is a value rewrite, not a schema
    /// change; set-based, and idempotent because a second run matches nothing.
    /// </para>
    /// </summary>
    public partial class CollapseSpeechItemType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE ParagraphItems
                SET ItemType = 'Speech'
                WHERE ItemType IN ('Narration', 'Character')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Which speech rows were 'Narration' is only recoverable from the
            // speaker, and reconstructing the split here would recreate the very disagreement
            // between type and speaker that ADR-0006 removed.
        }
    }
}
