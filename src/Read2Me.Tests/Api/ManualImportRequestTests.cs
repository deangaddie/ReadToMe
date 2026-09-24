using Read2Me.App.Api;
using Read2Me.Core.Models;
using Xunit;

namespace Read2Me.Tests.Api
{
    /// <summary>
    /// The manual-import body's translation into <see cref="ManualReadOptions"/>: the same gates the
    /// Blazor form applies, plus the wire's own mode names.
    /// </summary>
    public class ManualImportRequestTests
    {
        private static SplitRuleRequest Rule(string mode, string? prefix = null) => new(mode, prefix);

        [Fact]
        public void ChaptersOnly_MapsTheWireModeNames()
        {
            var request = new ManualImportRequest(false, false, null, null, Rule("Roman"));

            Assert.True(request.TryToOptions(out var options, out var error));
            Assert.Null(error);
            Assert.False(options!.HasVolumes);
            Assert.False(options.HasParts);
            Assert.Null(options.VolumeRule);
            Assert.Null(options.PartRule);
            Assert.Equal(new SectionSplitRule(SplitDetectionMode.RomanNumeral, null), options.ChapterRule);
        }

        [Theory]
        [InlineData("arabic", SplitDetectionMode.Number)]
        [InlineData("ROMAN", SplitDetectionMode.RomanNumeral)]
        [InlineData(" prefix ", SplitDetectionMode.Prefix)]
        public void ModeNames_AreCaseInsensitive(string text, SplitDetectionMode expected)
        {
            Assert.True(SplitRuleRequest.TryParseMode(text, out var mode));
            Assert.Equal(expected, mode);
        }

        [Theory]
        [InlineData("Number")]
        [InlineData("RomanNumeral")]
        [InlineData("")]
        [InlineData(null)]
        public void OnlyTheWireNames_Parse(string? text)
        {
            Assert.False(SplitRuleRequest.TryParseMode(text, out _));
        }

        [Fact]
        public void APrefixRule_TrimsThePrefix_AndOtherModesDropIt()
        {
            var request = new ManualImportRequest(true, true,
                Rule("Prefix", "  Book "), Rule("Arabic", "ignored"), Rule("Prefix", "Chapter"));

            Assert.True(request.TryToOptions(out var options, out _));
            Assert.Equal(new SectionSplitRule(SplitDetectionMode.Prefix, "Book"), options!.VolumeRule);
            Assert.Equal(new SectionSplitRule(SplitDetectionMode.Number, null), options.PartRule);
        }

        [Fact]
        public void ASwitchedOffLevel_IsIgnoredEvenWhenItsRuleIsInvalid()
        {
            var request = new ManualImportRequest(false, false,
                Rule("Prefix", ""), Rule("nonsense"), Rule("Arabic"));

            Assert.True(request.TryToOptions(out var options, out _));
            Assert.Null(options!.VolumeRule);
            Assert.Null(options.PartRule);
        }

        [Theory]
        [InlineData(true, false, "Prefix", " ", "Volume prefix cannot be empty.")]
        [InlineData(false, true, "Prefix", null, "Part prefix cannot be empty.")]
        [InlineData(true, false, "Sideways", null, "Volume detection mode must be one of Prefix, Arabic, Roman.")]
        public void ASwitchedOnLevel_NeedsAValidRule(bool volumes, bool parts, string mode, string? prefix, string expected)
        {
            var request = new ManualImportRequest(volumes, parts, Rule(mode, prefix), Rule(mode, prefix), Rule("Roman"));

            Assert.False(request.TryToOptions(out var options, out var error));
            Assert.Null(options);
            Assert.Equal(expected, error);
        }

        [Fact]
        public void ChapterRule_IsAlwaysRequired()
        {
            Assert.False(new ManualImportRequest(false, false, null, null, null).TryToOptions(out _, out var missing));
            Assert.Equal("Chapter detection is required.", missing);

            Assert.False(new ManualImportRequest(false, false, null, null, Rule("Prefix", "")).TryToOptions(out _, out var blank));
            Assert.Equal("Chapter prefix cannot be empty.", blank);
        }
    }
}
