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

        [Fact]
        public async Task The_other_books_are_written_in_Series_position_order()
        {
            var written = new List<string>();

            var results = await SeriesCopies.RunAsync(
                [Book("adventures", 3), Book("four", 2), Book("novella", 1.5m)],
                book =>
                {
                    written.Add(book.Folder);
                    return Task.FromResult<string?>(null);
                });

            Assert.Equal(["novella", "four", "adventures"], written);
            Assert.All(results, r => Assert.Null(r.Failure));
        }

        [Fact]
        public async Task A_failed_copy_is_recorded_and_the_rest_carry_on()
        {
            var written = new List<string>();

            var results = await SeriesCopies.RunAsync(
                [Book("four", 2), Book("valley", 4), Book("adventures", 3)],
                book =>
                {
                    written.Add(book.Folder);
                    return book.Folder switch
                    {
                        "four" => Task.FromResult<string?>("Another write to 'four' is still in progress."),
                        "adventures" => throw new IOException("disk full"),
                        _ => Task.FromResult<string?>(null),
                    };
                });

            Assert.Equal(["four", "adventures", "valley"], written);
            Assert.Equal(
                [("four", (string?)"Another write to 'four' is still in progress."), ("adventures", "disk full"), ("valley", null)],
                results.Select(r => (r.Folder, r.Failure)).ToArray());
        }

        // ---- delete refusals -----------------------------------------------------------------------

        private static SeriesVoiceDeleteBook Uses(string folder, int rules) => new(folder, folder, rules);

        [Fact]
        public void Another_books_positional_rules_refuse_the_delete_naming_only_those_books()
        {
            var block = SeriesVoiceDeleteRules.Check(isLastVoice: false, [Uses("four", 0), Uses("adventures", 2)]);

            Assert.NotNull(block);
            Assert.Equal(SeriesVoiceDeleteReason.PositionalRules, block.Reason);
            Assert.Equal([Uses("adventures", 2)], block.Books);
        }

        [Fact]
        public void The_last_Voice_is_refused_while_other_books_would_lose_their_default()
        {
            var block = SeriesVoiceDeleteRules.Check(isLastVoice: true, [Uses("four", 0), Uses("adventures", 0)]);

            Assert.NotNull(block);
            Assert.Equal(SeriesVoiceDeleteReason.LastVoice, block.Reason);
            Assert.Equal(["four", "adventures"], block.Books.Select(b => b.Folder));
        }

        [Fact]
        public void Rules_are_named_before_the_last_Voice()
        {
            var block = SeriesVoiceDeleteRules.Check(isLastVoice: true, [Uses("four", 1)]);

            Assert.Equal(SeriesVoiceDeleteReason.PositionalRules, block?.Reason);
        }

        [Fact]
        public void A_Voice_no_other_book_uses_can_go_and_so_can_the_last_one_of_a_Character_linked_nowhere_else()
        {
            Assert.Null(SeriesVoiceDeleteRules.Check(isLastVoice: false, [Uses("four", 0)]));
            Assert.Null(SeriesVoiceDeleteRules.Check(isLastVoice: true, []));
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
