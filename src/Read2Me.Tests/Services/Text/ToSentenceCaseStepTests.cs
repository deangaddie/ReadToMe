using Read2Me.Services.Text;
using Xunit;

namespace Read2Me.Tests.Services.Text;

public class ToSentenceCaseStepTests
{
    [Fact]
    public void BothOff_ReturnsInputUnchanged()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("HELLO WORLD", step.Process("HELLO WORLD"));
    }

    // --- Paragraph normalisation ---

    [Fact]
    public void Paragraph_AllCaps_ConvertedToSentenceCase()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("Hello world", step.Process("HELLO WORLD"));
    }

    [Fact]
    public void Paragraph_AllCapsWithDigits_ConvertedToSentenceCase()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("Chapter 3", step.Process("CHAPTER 3"));
    }

    [Fact]
    public void Paragraph_MixedCase_Unchanged()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("Hello World", step.Process("Hello World"));
    }

    [Fact]
    public void Paragraph_LeadingPunctuation_FirstLetterUppercased()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("\"Hello world\"", step.Process("\"HELLO WORLD\""));
    }

    [Fact]
    public void Paragraph_Fires_WordStepNotApplied()
    {
        // all-caps paragraph + word enabled: paragraph fires, result is sentence case, not further de-shouted
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: true, wordMinLength: 3);
        Assert.Equal("Hello world", step.Process("HELLO WORLD"));
    }

    // --- Word de-shouting ---

    [Fact]
    public void Word_AtThreshold_Lowercased()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("He said loudly", step.Process("He said LOUDLY"));
    }

    [Fact]
    public void Word_BelowThreshold_Unchanged()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("It was OK", step.Process("It was OK"));
    }

    [Fact]
    public void Word_PunctuationAttached_WholeTokenLowercased()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("world,", step.Process("WORLD,"));
    }

    [Fact]
    public void Word_HyphenatedToken_TreatedAsSingleToken()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("hello-world", step.Process("HELLO-WORLD"));
    }

    // --- All-caps detection ---

    [Fact]
    public void AllCapsDetection_DigitsOnlyString_NotTreatedAsAllCaps()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("123", step.Process("123"));
    }

    [Fact]
    public void AllCapsDetection_PunctuationOnlyString_NotTreatedAsAllCaps()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("...", step.Process("..."));
    }

    [Fact]
    public void AllCapsDetection_NonLettersIgnored_AllCapsLetters_Fires()
    {
        // "HELLO 123" — digits ignored, all letters are uppercase → fires
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("Hello 123", step.Process("HELLO 123"));
    }

    // --- Whitespace preservation ---

    [Fact]
    public void Word_DoubleSpace_Preserved()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("Hello  world", step.Process("Hello  WORLD"));
    }

    [Fact]
    public void Word_TabSeparator_Preserved()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("Hello\tworld", step.Process("Hello\tWORLD"));
    }

    [Fact]
    public void Word_NewlineSeparator_Preserved()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("Hello\nworld", step.Process("Hello\nWORLD"));
    }

    // --- Edge cases ---

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: true, wordMinLength: 1);
        Assert.Equal("", step.Process(""));
    }

    [Fact]
    public void SingleUppercaseLetter_ParagraphEnabled_Lowercased_ThenUppercased_SameChar()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("A", step.Process("A"));
    }

    [Fact]
    public void SingleLowercaseLetter_ParagraphEnabled_Unchanged()
    {
        // lowercase 'a' is not all-caps
        var step = new ToSentenceCaseStep(paragraphEnabled: true, wordEnabled: false, wordMinLength: 5);
        Assert.Equal("a", step.Process("a"));
    }

    [Fact]
    public void SingleWord_AllCaps_AtThreshold_Lowercased()
    {
        var step = new ToSentenceCaseStep(paragraphEnabled: false, wordEnabled: true, wordMinLength: 5);
        Assert.Equal("hello", step.Process("HELLO"));
    }
}
