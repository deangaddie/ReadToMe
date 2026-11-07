using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Series;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// Shared Voice edits (#221, ADR 0018): which commits a series copies, the order the other
    /// linked books are written in, a failed copy recorded with the rest carried on, and when
    /// deleting a shared Voice is refused.
    /// </summary>
    public class SeriesVoiceCopyRulesTests
    {
        private static readonly ProjectFolderId Folder = new("scarlet");
        private static readonly Guid VoiceId = Guid.NewGuid();

        private static SeriesPeer Book(string folder, decimal position) => new(folder, folder, position, Guid.NewGuid());

        // ---- which commits are copied ----------------------------------------------------------------

        [Fact]
        public void Voice_edits_are_copied_and_a_new_Voice_is_copied_for_its_Character()
        {
            var watson = Guid.NewGuid();
            Assert.Equal(new SeriesVoiceEdits.Created(watson), SeriesVoiceEdits.Of(new CreateVoiceMutation(Folder, watson, "Army doctor")));
            Assert.Equal(new SeriesVoiceEdits.Changed(VoiceId), SeriesVoiceEdits.Of(new UpdateVoiceMutation(Folder, VoiceId, "Old campaigner", null)));
            Assert.Equal(new SeriesVoiceEdits.Changed(VoiceId), SeriesVoiceEdits.Of(new SetVoiceAudioMutation(Folder, VoiceId, "voices/a.wav")));
            Assert.Equal(new SeriesVoiceEdits.Changed(VoiceId), SeriesVoiceEdits.Of(new ApplyVoiceAudioEditMutation(Folder, VoiceId)));
            Assert.Equal(new SeriesVoiceEdits.Changed(VoiceId), SeriesVoiceEdits.Of(new SetVoiceSourceMutation(Folder, VoiceId, true)));
            Assert.Equal(new SeriesVoiceEdits.Deleted(VoiceId), SeriesVoiceEdits.Of(new DeleteVoiceMutation(Folder, VoiceId)));
        }

        [Fact]
        public void A_Restore_is_never_copied()
        {
            var restore = new RestoreStepMutation(Folder, null!, null!);

            Assert.Null(SeriesVoiceEdits.Of(restore));
            Assert.False(SeriesVoiceEdits.IsSeriesCopy(restore));
        }

        [Fact]
        public void A_books_default_and_its_Voice_Rules_stay_its_own()
        {
            Assert.Null(SeriesVoiceEdits.Of(new SetVoiceDefaultMutation(Folder, VoiceId)));
            Assert.Null(SeriesVoiceEdits.Of(new DeleteVoiceRuleMutation(Folder, Guid.NewGuid())));
        }

        [Fact]
        public void The_series_own_writes_are_the_copies_and_are_not_copied_on()
        {
            var copy = new SeriesVoiceCopy(VoiceId, "Army doctor", null, Read2Me.Data.Enums.VoiceSource.Uploaded,
                null, null, null, null, null, DateTime.UtcNow);

            Assert.True(SeriesVoiceEdits.IsSeriesCopy(new CopySeriesVoiceMutation(Folder, Guid.NewGuid(), copy, "Edit voice", StepAction.Voice)));
            Assert.True(SeriesVoiceEdits.IsSeriesCopy(new GainSeriesVoicesMutation(Folder, Guid.NewGuid(), [copy])));
        }

        // ---- write order -------------------------------------------------------------------------

        // A failed copy is recorded and the rest carry on: SeriesVoiceReplicatorTests.

        [Fact]
        public void The_other_books_are_written_in_Series_position_order()
        {
            var order = SeriesCopies.InWriteOrder([Book("adventures", 3), Book("four", 2), Book("novella", 1.5m)]);

            Assert.Equal(["novella", "four", "adventures"], order.Select(b => b.Folder));
        }

        // ---- delete refusals -----------------------------------------------------------------------

        /// <summary>Another linked book: its positional rules on the Voice, and whether it holds the Voice and any other.</summary>
        private static SeriesVoiceDeleteRules.OtherBook Other(string folder, int rules = 0, bool holds = true, bool others = true) =>
            new(folder, folder, rules, holds, others);

        [Fact]
        public void Another_books_positional_rules_refuse_the_delete_naming_only_those_books()
        {
            var block = SeriesVoiceDeleteRules.Check([Other("four"), Other("adventures", rules: 2)]);

            Assert.NotNull(block);
            Assert.Equal(SeriesVoiceDeleteReason.PositionalRules, block.Reason);
            Assert.Equal([new SeriesVoiceDeleteBook("adventures", "adventures", 2)], block.Books);
        }

        [Fact]
        public void The_last_Voice_is_refused_while_other_books_would_lose_their_default()
        {
            var block = SeriesVoiceDeleteRules.Check([Other("four", others: false), Other("adventures", others: false)]);

            Assert.NotNull(block);
            Assert.Equal(SeriesVoiceDeleteReason.LastVoice, block.Reason);
            Assert.Equal(["four", "adventures"], block.Books.Select(b => b.Folder));
        }

        [Fact]
        public void Only_a_book_that_holds_the_Voice_and_no_other_would_lose_its_default()
        {
            // "four" differs: it never got the Voice. "adventures" differs too: it holds another as well.
            var block = SeriesVoiceDeleteRules.Check([
                Other("four", holds: false, others: false), Other("adventures", others: true), Other("valley", others: false),
            ]);

            Assert.Equal(SeriesVoiceDeleteReason.LastVoice, block?.Reason);
            Assert.Equal(["valley"], block!.Books.Select(b => b.Folder));
            Assert.Null(SeriesVoiceDeleteRules.Check([Other("four", holds: false, others: false), Other("adventures")]));
        }

        [Fact]
        public void Rules_are_named_before_the_last_Voice()
        {
            var block = SeriesVoiceDeleteRules.Check([Other("four", rules: 1, others: false)]);

            Assert.Equal(SeriesVoiceDeleteReason.PositionalRules, block?.Reason);
        }

        [Fact]
        public void A_Voice_no_other_book_uses_can_go_and_so_can_the_last_one_of_a_Character_linked_nowhere_else()
        {
            Assert.Null(SeriesVoiceDeleteRules.Check([Other("four")]));
            Assert.Null(SeriesVoiceDeleteRules.Check([]));
        }

        [Fact]
        public void The_refusal_names_the_Voice_and_the_books()
        {
            var detail = SeriesVoiceDeleteRules.Describe("Army doctor",
                new SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason.PositionalRules, [new("four", "The Sign of the Four", 1)]));

            Assert.Contains("\"Army doctor\"", detail);
            Assert.Contains("'The Sign of the Four'", detail);
        }
    }
}
