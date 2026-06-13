using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books;

public class ParagraphSplitterTests
{
    // --- null / empty ---

    [Fact]
    public void NullInput_ReturnsSingleNarration()
    {
        var result = ParagraphSplitter.Split(null!);
        Assert.Single(result);
        Assert.Equal(SegmentType.Narration, result[0].Type);
        Assert.Equal(string.Empty, result[0].Text);
    }

    [Fact]
    public void EmptyInput_ReturnsSingleNarration()
    {
        var result = ParagraphSplitter.Split(string.Empty);
        Assert.Single(result);
        Assert.Equal(SegmentType.Narration, result[0].Type);
    }

    [Fact]
    public void PureNarration_ReturnsSingleNarrationSegment()
    {
        var result = ParagraphSplitter.Split("He walked into the room.");
        Assert.Single(result);
        Assert.Equal(SegmentType.Narration, result[0].Type);
        Assert.Equal("He walked into the room.", result[0].Text);
    }

    // --- ASCII double quotes ---

    [Fact]
    public void AsciiDoubleQuote_SingleDialogue_ThreeSegments()
    {
        var result = ParagraphSplitter.Split("She said \"hello\" and left.");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Narration, result[0].Type);
        Assert.Equal("She said ", result[0].Text);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("\"hello\"", result[1].Text);
        Assert.Equal(SegmentType.Narration, result[2].Type);
        Assert.Equal(" and left.", result[2].Text);
    }

    [Fact]
    public void AsciiDoubleQuote_DialogueAtStart_TwoSegments()
    {
        var result = ParagraphSplitter.Split("\"Go away!\" he shouted.");
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[0].Type);
        Assert.Equal("\"Go away!\"", result[0].Text);
        Assert.Equal(SegmentType.Narration, result[1].Type);
    }

    [Fact]
    public void AsciiDoubleQuote_DialogueAtEnd_TwoSegments()
    {
        var result = ParagraphSplitter.Split("He whispered \"come here\"");
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentType.Narration, result[0].Type);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("\"come here\"", result[1].Text);
    }

    [Fact]
    public void AsciiDoubleQuote_MultipleDialogueRuns_AlternateSegments()
    {
        var result = ParagraphSplitter.Split("\"Hi\" she said \"bye\".");
        Assert.Equal(4, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[0].Type);
        Assert.Equal(SegmentType.Narration, result[1].Type);
        Assert.Equal(SegmentType.Dialogue, result[2].Type);
        Assert.Equal(SegmentType.Narration, result[3].Type);
    }

    // --- curly double quotes ---

    [Fact]
    public void CurlyDoubleQuote_SingleDialogue_ThreeSegments()
    {
        var result = ParagraphSplitter.Split("She said “hello” and left.");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("“hello”", result[1].Text);
    }

    [Fact]
    public void CurlyDoubleQuote_PreservesQuoteChars()
    {
        var result = ParagraphSplitter.Split("“Test”");
        Assert.Single(result);
        Assert.Equal(SegmentType.Dialogue, result[0].Type);
        Assert.Equal("“Test”", result[0].Text);
    }

    // --- curly single quotes ---

    [Fact]
    public void CurlySingleQuote_SingleDialogue_ThreeSegments()
    {
        var result = ParagraphSplitter.Split("He muttered ‘aye’ quietly.");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("‘aye’", result[1].Text);
    }

    // --- ASCII single quotes (apostrophe vs dialogue) ---

    [Fact]
    public void AsciiSingleQuote_Apostrophe_NotTreatedAsDialogue()
    {
        var result = ParagraphSplitter.Split("It's a fine day.");
        Assert.Single(result);
        Assert.Equal(SegmentType.Narration, result[0].Type);
    }

    [Fact]
    public void AsciiSingleQuote_PossessiveApostrophe_NotDialogue()
    {
        var result = ParagraphSplitter.Split("John's hat was red.");
        Assert.Single(result);
        Assert.Equal(SegmentType.Narration, result[0].Type);
    }

    [Fact]
    public void AsciiSingleQuote_OpenNotPrecededByLetter_IsDialogue()
    {
        var result = ParagraphSplitter.Split("She said 'hello' to him.");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("'hello'", result[1].Text);
    }

    [Fact]
    public void AsciiSingleQuote_CloseFollowedByLetter_NotEndOfDialogue()
    {
        // "don't" — apostrophe followed by 't', so inner loop won't break
        var result = ParagraphSplitter.Split("She said 'don't go'.");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("'don't go'", result[1].Text);
    }

    [Fact]
    public void AsciiSingleQuote_AtStartOfText_IsDialogue()
    {
        var result = ParagraphSplitter.Split("'Run!' she cried.");
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[0].Type);
        Assert.Equal("'Run!'", result[0].Text);
    }

    // --- mixed quote styles ---

    [Fact]
    public void MixedQuotes_DoubleAndSingle_BothDetected()
    {
        // "He said " + "\"stop\"" + " then " + "'go'" + "."
        var result = ParagraphSplitter.Split("He said \"stop\" then 'go'.");
        Assert.Equal(5, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("\"stop\"", result[1].Text);
        Assert.Equal(SegmentType.Dialogue, result[3].Type);
        Assert.Equal("'go'", result[3].Text);
    }

    // --- edge cases ---

    [Fact]
    public void TextWithNoAlphanumeric_ReturnsSingleNarration()
    {
        var result = ParagraphSplitter.Split("... ...");
        Assert.Single(result);
        Assert.Equal(SegmentType.Narration, result[0].Type);
    }

    [Fact]
    public void UnterminatedAsciiDoubleQuote_CapturesRest()
    {
        var result = ParagraphSplitter.Split("He said \"help");
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("\"help", result[1].Text);
    }

    [Fact]
    public void UnterminatedCurlyDoubleQuote_CapturesRest()
    {
        var result = ParagraphSplitter.Split("He said “help");
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
    }

    [Fact]
    public void OnlyDialogue_ReturnsSingleDialogueSegment()
    {
        var result = ParagraphSplitter.Split("\"All dialogue here.\"");
        Assert.Single(result);
        Assert.Equal(SegmentType.Dialogue, result[0].Type);
    }

    [Fact]
    public void EmptyQuotes_ReturnsDialogueSegmentWithJustQuotes()
    {
        var result = ParagraphSplitter.Split("She said \"\".");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
        Assert.Equal("\"\"", result[1].Text);
    }

    [Fact]
    public void ContractionsAndDialogueMixed_CorrectlySegmented()
    {
        var result = ParagraphSplitter.Split("He couldn't believe she said \"I'm fine\" today.");
        Assert.Equal(3, result.Count);
        Assert.Equal(SegmentType.Narration, result[0].Type);
        Assert.Contains("couldn't", result[0].Text);
        Assert.Equal(SegmentType.Dialogue, result[1].Type);
    }
}
