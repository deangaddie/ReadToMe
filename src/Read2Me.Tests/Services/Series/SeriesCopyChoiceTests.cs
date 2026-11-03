using Read2Me.Data.Enums;
using Read2Me.Services.Series;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// Differs from series (#222, #205 item 5): each linked book's Voices are compared by their
    /// shared fields and their reference audio's content, and the series copy is the version most
    /// linked books hold, a tie going to the lowest Series position.
    /// </summary>
    public class SeriesCopyChoiceTests
    {
        private static readonly Guid Doctor = Guid.NewGuid();
        private static readonly Guid Campaigner = Guid.NewGuid();

        private static SeriesVoiceState Voice(Guid id, string name = "Army doctor", string? audioHash = "aaaa") => new(
            id, name, "warm", VoiceSource.Generated, "A warm army doctor.", "It is a capital mistake.",
            "{\"cfg\":2}", "{\"temperature\":0.5}", audioHash);

        private static SeriesCopyChoice.Book Book(string folder, decimal position, params SeriesVoiceState[] voices) =>
            new(folder, position, SeriesVoiceVersion.Of(voices));

        // ---- the version -------------------------------------------------------------------------

        [Fact]
        public void The_same_Voices_in_any_order_are_one_version()
        {
            Assert.Equal(
                SeriesVoiceVersion.Of([Voice(Doctor), Voice(Campaigner, "Old campaigner")]),
                SeriesVoiceVersion.Of([Voice(Campaigner, "Old campaigner"), Voice(Doctor)]));
        }

        [Fact]
        public void A_changed_field_is_another_version()
        {
            Assert.NotEqual(SeriesVoiceVersion.Of([Voice(Doctor)]), SeriesVoiceVersion.Of([Voice(Doctor, "Old campaigner")]));
            Assert.NotEqual(SeriesVoiceVersion.Of([Voice(Doctor)]), SeriesVoiceVersion.Of([Voice(Doctor) with { Transcript = "Elementary." }]));
            Assert.NotEqual(SeriesVoiceVersion.Of([Voice(Doctor)]), SeriesVoiceVersion.Of([Voice(Doctor) with { TtsSettingsOverrideJson = null }]));
        }

        [Fact]
        public void Changed_audio_alone_is_another_version()
        {
            Assert.NotEqual(SeriesVoiceVersion.Of([Voice(Doctor, audioHash: "aaaa")]), SeriesVoiceVersion.Of([Voice(Doctor, audioHash: "bbbb")]));
            Assert.NotEqual(SeriesVoiceVersion.Of([Voice(Doctor, audioHash: "aaaa")]), SeriesVoiceVersion.Of([Voice(Doctor, audioHash: null)]));
        }

        [Fact]
        public void A_Voice_more_or_less_is_another_version()
        {
            Assert.NotEqual(SeriesVoiceVersion.Of([Voice(Doctor)]), SeriesVoiceVersion.Of([Voice(Doctor), Voice(Campaigner)]));
        }

        // ---- the choice --------------------------------------------------------------------------

        [Fact]
        public void Books_that_all_hold_one_version_differ_in_none_and_the_lowest_is_the_series_copy()
        {
            var choice = SeriesCopyChoice.Choose([Book("four", 2, Voice(Doctor)), Book("scarlet", 1, Voice(Doctor)), Book("adventures", 3, Voice(Doctor))]);

            Assert.Equal("scarlet", choice.SeriesCopyFolder);
            Assert.Empty(choice.Differs);
        }

        [Fact]
        public void The_version_most_books_hold_is_the_series_copy_even_when_the_lowest_book_differs()
        {
            var choice = SeriesCopyChoice.Choose([
                Book("scarlet", 1, Voice(Doctor, "Restored")),
                Book("four", 2, Voice(Doctor)),
                Book("adventures", 3, Voice(Doctor)),
            ]);

            Assert.Equal("four", choice.SeriesCopyFolder);
            Assert.Equal(["scarlet"], choice.Differs);
        }

        [Fact]
        public void A_tie_goes_to_the_version_of_the_lowest_Series_position()
        {
            var choice = SeriesCopyChoice.Choose([
                Book("adventures", 3, Voice(Doctor)),
                Book("four", 2, Voice(Doctor, "Old campaigner")),
                Book("novella", 1.5m, Voice(Doctor)),
                Book("scarlet", 1, Voice(Doctor, "Old campaigner")),
            ]);

            Assert.Equal("scarlet", choice.SeriesCopyFolder);
            Assert.Equal(["adventures", "novella"], choice.Differs.Order());
        }

        [Fact]
        public void With_every_book_its_own_version_all_but_the_lowest_differ()
        {
            var choice = SeriesCopyChoice.Choose([
                Book("four", 2, Voice(Doctor, audioHash: "bbbb")),
                Book("scarlet", 1, Voice(Doctor, audioHash: "aaaa")),
                Book("adventures", 3, Voice(Doctor, audioHash: "cccc")),
            ]);

            Assert.Equal("scarlet", choice.SeriesCopyFolder);
            Assert.Equal(["adventures", "four"], choice.Differs.Order());
            Assert.True(choice.DiffersFrom("four"));
            Assert.False(choice.DiffersFrom("scarlet"));
        }
    }
}
