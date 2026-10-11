using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The speech-marker classifier on its own: text in, key out. The cases come from the
    /// released-narration corpus findings. Whether a key repeats often enough to release anything
    /// is <see cref="NarrationRelease"/>'s gate, tested through the handler.
    /// </summary>
    public class SpeechMarkersTests
    {
        private static readonly IReadOnlySet<string> Roster =
            SpeechMarkers.Roster(["Narrator", "Hari Seldon", "Seldon", "Jessica", "Lady Jessica"]);

        [Theory]
        [InlineData("Q. Who are you?", "Q.")]
        [InlineData("A: Hari Seldon.", "A:")]
        [InlineData("Q: Who are you?", "Q:")]
        [InlineData("GD. The commission rules.", "GD.")]
        [InlineData("Seldon: I am.", "Seldon:")]
        [InlineData("SELDON: I am.", "SELDON:")]
        [InlineData("Lady Jessica: The sleeper must awaken.", "Lady Jessica:")]
        [InlineData("—What is the law?— the voice asked.", "—…—")]
        [InlineData("He thought —I am!— and cried.", "—…—")]
        [InlineData("Lucy—I call the thing by its name—saw it at once.", "—…—")]
        [InlineData("Then,—Come here— she said.", "—…—")]
        public void KeyOf_SpeechMarkedText_IsTheMarker(string text, string expected) =>
            Assert.Equal(expected, SpeechMarkers.KeyOf(text, Roster));

        [Theory]
        [InlineData("Iron: Strength +2")]
        [InlineData("7 September.—I am writing this by the light of a candle.—")]
        [InlineData("The night was cold and nobody spoke.")]
        [InlineData("The Very Old Lady Jessica: words.")]
        [InlineData("ABC. text")]
        [InlineData("Q.")]
        [InlineData("Q.No space after the initial.")]
        [InlineData("—lower case after the dash— he said.")]
        [InlineData("")]
        public void KeyOf_UnmarkedText_IsNull(string text) =>
            Assert.Null(SpeechMarkers.KeyOf(text, Roster));

        [Fact]
        public void KeyOf_InitialPrefixWinsOverAnEmDashLaterInTheParagraph() =>
            Assert.Equal("Q.", SpeechMarkers.KeyOf("Q. And then —Who?— he asked.", Roster));

        [Fact]
        public void KeyOf_NamePrefixOffTheRoster_FallsThroughToEmDashSpeech() =>
            Assert.Equal("—…—", SpeechMarkers.KeyOf("Iron: —Strike!— it says.", Roster));

        [Fact]
        public void KeyOf_EmDashSpeechLongerThanTheLimit_IsNull() =>
            Assert.Null(SpeechMarkers.KeyOf("—A" + new string('x', 601) + "— he said.", Roster));

        [Fact]
        public void KeyOf_EmptyRoster_NeverKeysANamePrefix() =>
            Assert.Null(SpeechMarkers.KeyOf("Seldon: I am.", SpeechMarkers.Roster([])));
    }
}
