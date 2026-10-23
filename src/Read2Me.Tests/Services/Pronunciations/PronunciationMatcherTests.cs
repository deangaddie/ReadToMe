using Read2Me.Data.Entities;
using Read2Me.Services.Pronunciations;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

public class PronunciationMatcherTests
{
    private static readonly Pronunciation Kulgan = Entry("Kulgan", "KULL-gun");
    private static readonly Pronunciation Hermione = Entry("Hermione", "Her-MY-oh-nee");
    private static readonly Pronunciation MinasTirith = Entry("Minas Tirith", "MEE-nas TEER-ith");
    private static readonly Pronunciation Tirith = Entry("Tirith", "TEER-ith");
    private static readonly Pronunciation Eowyn = Entry("Éowyn", "AY-oh-win");

    private static Pronunciation Entry(string written, string spoken) =>
        new() { Id = Guid.NewGuid(), WrittenForm = written, SpokenForm = spoken };

    [Theory]
    // Possessive and punctuation neighbours: the characters either side are not letters or digits.
    [InlineData("Kulgan's tower.", "KULL-gun's tower.")]
    [InlineData("Master Kulgan, wait!", "Master KULL-gun, wait!")]
    [InlineData("“Kulgan” he said", "“KULL-gun” he said")]
    [InlineData("a Kulgan-like frown", "a KULL-gun-like frown")]
    // No partial words.
    [InlineData("A Kulganite and MacKulgan.", "A Kulganite and MacKulgan.")]
    [InlineData("Kulgan2 and 2Kulgan", "Kulgan2 and 2Kulgan")]
    // Twice, left to right.
    [InlineData("Kulgan and Kulgan", "KULL-gun and KULL-gun")]
    public void WholeWordsOnly(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Kulgan]).SpokenTextOf(text));
    }

    [Theory]
    // Title case: the spoken form's first letter follows, the rest stays as entered.
    [InlineData("Kulgan came.", "KULL-gun came.")]
    // Lower case: only the first letter follows, so the stress capitals survive.
    [InlineData("kulgan came.", "kULL-gun came.")]
    // All upper: the whole spoken form is upper-cased.
    [InlineData("KULGAN!", "KULL-GUN!")]
    // Mixed: the first letter rules.
    [InlineData("kULGAN", "kULL-gun")]
    public void TheMatchsCaseCarriesOver(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Kulgan]).SpokenTextOf(text));
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
    [InlineData("to Minas Tirith.", "to MEE-nas TEER-ith.")]
    [InlineData("to Minas\n  Tirith.", "to MEE-nas TEER-ith.")]
    [InlineData("to Minas\tTirith.", "to MEE-nas TEER-ith.")]
    [InlineData("to MinasTirith.", "to MinasTirith.")]
    public void AMultiWordForm_MatchesAnyRunOfWhitespace(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([MinasTirith]).SpokenTextOf(text));
    }

    [Theory]
    [InlineData("Minas Tirith fell.", "MEE-nas TEER-ith fell.")]
    [InlineData("Tirith fell.", "TEER-ith fell.")]
    [InlineData("Minas Tirith and Tirith", "MEE-nas TEER-ith and TEER-ith")]
    public void TheLongerFormWins_WhereTwoOverlap(string text, string spoken)
    {
        // Listed shorter first: precedence is length, not order.
        Assert.Equal(spoken, new PronunciationMatcher([Tirith, MinasTirith]).SpokenTextOf(text));
    }

    [Fact]
    public void ALongerFormThatFailsItsWordEdge_LeavesTheShorterOneToMatch()
    {
        var matcher = new PronunciationMatcher([Entry("Minas", "MEE-nas"), Entry("Minas Tir", "wrong")]);

        Assert.Equal("MEE-nas Tirith", matcher.SpokenTextOf("Minas Tirith"));
    }

    [Fact]
    public void ASpokenForm_IsNeverRescanned()
    {
        // "Pug" → "Kulgan" must not then become "KULL-gun".
        var matcher = new PronunciationMatcher([Entry("Pug", "Kulgan"), Kulgan]);

        Assert.Equal("Kulgan met KULL-gun.", matcher.SpokenTextOf("Pug met Kulgan."));
    }

    [Theory]
    [InlineData("Éowyn rode.", "AY-oh-win rode.")]
    [InlineData("éowyn rode.", "aY-oh-win rode.")]
    [InlineData("ÉOWYN!", "AY-OH-WIN!")]
    [InlineData("Éowynë rode.", "Éowynë rode.")]
    public void UnicodeLetters_AreLettersForCaseAndForWordEdges(string text, string spoken)
    {
        Assert.Equal(spoken, new PronunciationMatcher([Eowyn]).SpokenTextOf(text));
    }

    [Fact]
    public void AnEmptyList_LeavesTheTextAsItIs_WithNoMatches()
    {
        var matcher = new PronunciationMatcher([]);

        var spoken = matcher.Apply("Kulgan came.");

        Assert.Equal("Kulgan came.", spoken.Text);
        Assert.Empty(spoken.Matches);
    }

    [Fact]
    public void Matches_AreSpansInTheOriginalText_NamingTheirEntry()
    {
        var matcher = new PronunciationMatcher([Kulgan, Tirith, MinasTirith]);
        const string text = "Kulgan's road to Minas  Tirith.";

        var spoken = matcher.Apply(text);

        Assert.Equal(
        [
            new PronunciationMatch(0, 6, Kulgan.Id),
            new PronunciationMatch(17, 13, MinasTirith.Id),
        ], spoken.Matches);
        Assert.Equal("KULL-gun's road to MEE-nas TEER-ith.", spoken.Text);
        Assert.Equal(spoken.Matches, matcher.MatchesIn(text));
    }

    [Fact]
    public void RegexCharactersInAWrittenForm_AreLiteral()
    {
        var matcher = new PronunciationMatcher([Entry("Dr. No", "Doctor No")]);

        Assert.Equal("Doctor No said.", matcher.SpokenTextOf("Dr. No said."));
        Assert.Equal("Drx No said.", matcher.SpokenTextOf("Drx No said."));
    }
}
