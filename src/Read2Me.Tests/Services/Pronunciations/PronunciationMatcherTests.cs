using Read2Me.Data.Entities;
using Read2Me.Services.Pronunciations;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

public class PronunciationMatcherTests
{
    private static readonly Pronunciation Watson = Entry("Watson", "WAT-sun");
    private static readonly Pronunciation Hermione = Entry("Hermione", "Her-MY-oh-nee");
    private static readonly Pronunciation VanHelsing = Entry("Van Helsing", "VAN HEL-sing");
    private static readonly Pronunciation Helsing = Entry("Helsing", "HEL-sing");
    private static readonly Pronunciation Emile = Entry("Émile", "AY-meel");

    private static Pronunciation Entry(string written, string spoken) =>
        new() { Id = Guid.NewGuid(), WrittenForm = written, SpokenForm = spoken };

    [Theory]
    // Possessive and punctuation neighbours: the characters either side are not letters or digits.
    [InlineData("Watson's tower.", "WAT-sun's tower.")]
    [InlineData("Master Watson, wait!", "Master WAT-sun, wait!")]
    [InlineData("“Watson” he said", "“WAT-sun” he said")]
    [InlineData("a Watson-like frown", "a WAT-sun-like frown")]
    // No partial words.
    [InlineData("A Watsonite and MacWatson.", "A Watsonite and MacWatson.")]
    [InlineData("Watson2 and 2Watson", "Watson2 and 2Watson")]
    // Twice, left to right.
    [InlineData("Watson and Watson", "WAT-sun and WAT-sun")]
    public void WholeWordsOnly(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Watson]).SpokenTextOf(text));
    }

    [Theory]
    // Title case: the spoken form's first letter follows, the rest stays as entered.
    [InlineData("Watson came.", "WAT-sun came.")]
    // Lower case: only the first letter follows, so the stress capitals survive.
    [InlineData("watson came.", "wAT-sun came.")]
    // All upper: the whole spoken form is upper-cased.
    [InlineData("WATSON!", "WAT-SUN!")]
    // Mixed: the first letter rules.
    [InlineData("wATSON", "wAT-sun")]
    public void TheMatchsCaseCarriesOver(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Watson]).SpokenTextOf(text));
    }

    [Theory]
    [InlineData("Hermione smiled.", "Her-MY-oh-nee smiled.")]
    [InlineData("said hermione", "said her-MY-oh-nee")]
    [InlineData("HERMIONE", "HER-MY-OH-NEE")]
    public void StressCapitals_SurviveTitleAndLowerCase(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Hermione]).SpokenTextOf(text));
    }

    [Theory]
    // One upper-case letter is a first letter, not a shout.
    [InlineData("I saw it.", "Eye saw it.")]
    [InlineData("i saw it.", "eye saw it.")]
    public void ASingleLetterMatch_FollowsTheFirstLetterRule(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Entry("I", "eye")]).SpokenTextOf(text));
    }

    [Theory]
    [InlineData("to Van Helsing.", "to VAN HEL-sing.")]
    [InlineData("to Van\n  Helsing.", "to VAN HEL-sing.")]
    [InlineData("to Van\tHelsing.", "to VAN HEL-sing.")]
    [InlineData("to VanHelsing.", "to VanHelsing.")]
    public void AMultiWordForm_MatchesAnyRunOfWhitespace(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([VanHelsing]).SpokenTextOf(text));
    }

    [Theory]
    [InlineData("Van Helsing fell.", "VAN HEL-sing fell.")]
    [InlineData("Helsing fell.", "HEL-sing fell.")]
    [InlineData("Van Helsing and Helsing", "VAN HEL-sing and HEL-sing")]
    public void TheLongerFormWins_WhereTwoOverlap(string text, string spoken)
    {
        // Listed shorter first: precedence is length, not order.
        Assert.Equal(spoken, new PronunciationMatcher([Helsing, VanHelsing]).SpokenTextOf(text));
    }

    [Fact]
    public void ALongerFormThatFailsItsWordEdge_LeavesTheShorterOneToMatch()
    {
        var matcher = new PronunciationMatcher([Entry("Van", "VAN"), Entry("Van Hel", "wrong")]);

        Assert.Equal("VAN Helsing", matcher.SpokenTextOf("Van Helsing"));
    }

    [Fact]
    public void ASpokenForm_IsNeverRescanned()
    {
        // "Holmes" → "Watson" must not then become "WAT-sun".
        var matcher = new PronunciationMatcher([Entry("Holmes", "Watson"), Watson]);

        Assert.Equal("Watson met WAT-sun.", matcher.SpokenTextOf("Holmes met Watson."));
    }

    [Theory]
    [InlineData("Émile rode.", "AY-meel rode.")]
    [InlineData("émile rode.", "aY-meel rode.")]
    [InlineData("ÉMILE!", "AY-MEEL!")]
    [InlineData("Émileë rode.", "Émileë rode.")]
    public void UnicodeLetters_AreLettersForCaseAndForWordEdges(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Emile]).SpokenTextOf(text));
    }

    [Fact]
    public void AnEmptyList_LeavesTheTextAsItIs_WithNoMatches()
    {
        var matcher = new PronunciationMatcher([]);

        var spoken = matcher.Apply("Watson came.");

        Assert.Equal("Watson came.", spoken.Text);
        Assert.Empty(spoken.Matches);
    }

    [Fact]
    public void Matches_AreSpansInTheOriginalText_NamingTheirEntry()
    {
        var matcher = new PronunciationMatcher([Watson, Helsing, VanHelsing]);
        const string text = "Watson's note to Van  Helsing.";

        var spoken = matcher.Apply(text);

        Assert.Equal(
        [
            new PronunciationMatch(0, 6, Watson.Id),
            new PronunciationMatch(17, 12, VanHelsing.Id),
        ], spoken.Matches);
        Assert.Equal("WAT-sun's note to VAN HEL-sing.", spoken.Text);
        Assert.Equal(spoken.Matches, matcher.MatchesIn(text));
    }

    [Fact]
    public void RegexCharactersInAWrittenForm_AreLiteral()
    {
        var matcher = new PronunciationMatcher([Entry("Dr. Seward", "Doctor Seward")]);

        Assert.Equal("Doctor Seward said.", matcher.SpokenTextOf("Dr. Seward said."));
        Assert.Equal("Drx Seward said.", matcher.SpokenTextOf("Drx Seward said."));
    }
}
