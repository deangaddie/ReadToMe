using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.Data.Migrations
{
    /// <summary>
    /// Makes "narration means the speaker is the narrator" true of every stored item.
    /// Import has always stamped the narrator on narration segments, but the chapter and part
    /// titles the app inserts never carried a speaker — this rescues those rows before anything
    /// is allowed to derive narration from the speaker alone.
    /// Data only: no schema change, and nothing reads the backfilled speaker yet.
    /// </summary>
    public partial class BackfillNarrationSpeakers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The seed Narrator character id (ProjectDbContext.NarratorId), written literally —
            // a migration records what was done, not what the constant says today.
            // Set-based, and idempotent: rows that already conform are not matched.
            migrationBuilder.Sql(
                """
                UPDATE ParagraphItems
                SET CharacterId = '00000000-0000-0000-0000-000000000001'
                WHERE ItemType = 'Narration' AND CharacterId IS NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Which narration rows had no speaker is not recoverable, and a
            // narration item stamped with the narrator is valid under the previous schema anyway.
        }
    }
}
