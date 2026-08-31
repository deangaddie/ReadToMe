using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <inheritdoc />
    public partial class OverwriteAttributionPromptsForFrozenItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0005 froze item boundaries: the four attribution templates now ask for speakers
            // per existing item, and the response schema only accepts that shape. A stored prompt
            // written against the old segment ask fail-parses EVERY paragraph, which reads on screen
            // as a model-quality problem rather than a migration problem — so this clears them
            // unconditionally, hand-edits included (spec §5).
            //
            // Clearing to NULL *is* "overwrite with the new default": null means "use the built-in
            // default" everywhere the prompts are read (LlmPromptService, and the settings page's
            // Reset), and it keeps the default text in one place instead of freezing a copy here
            // that would drift the moment PromptTemplates changes again.
            migrationBuilder.Sql(
                "UPDATE PromptSettings SET " +
                "CharacterPrompt = NULL, " +
                "BatchCharacterPrompt = NULL, " +
                "SimpleCharacterPrompt = NULL, " +
                "SimpleBatchCharacterPrompt = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible by design: the overwritten hand-edits were not copied anywhere.
        }
    }
}
