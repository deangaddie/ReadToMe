using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class EpubFileReaderTests
    {
        // ---------------------------------------------------------------
        // ParseHtml
        // ---------------------------------------------------------------

        [Fact]
        public void ParseHtml_EmptyString_ReturnsEmpty()
        {
            var result = EpubFileReader.ParseHtml(string.Empty);
            Assert.Empty(result);
        }

        [Fact]
        public void ParseHtml_WhitespaceOnly_ReturnsEmpty()
        {
            var result = EpubFileReader.ParseHtml("   \t\n  ");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseHtml_SingleParagraph_ReturnsSingleEntry()
        {
            var result = EpubFileReader.ParseHtml("<p>Hello</p>");
            Assert.Single(result);
            Assert.Equal("Hello", result[0].Text);
        }

        [Fact]
        public void ParseHtml_MultipleParagraphs_ReturnsAllInOrder()
        {
            var result = EpubFileReader.ParseHtml("<p>Hello</p><p>World</p>");
            Assert.Equal(2, result.Count);
            Assert.Equal("Hello", result[0].Text);
            Assert.Equal("World", result[1].Text);
        }

        [Fact]
        public void ParseHtml_WhitespaceOnlyParagraph_Skipped()
        {
            var result = EpubFileReader.ParseHtml("<p>   </p>");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseHtml_H1Heading_ReturnsSingleEntry()
        {
            var result = EpubFileReader.ParseHtml("<h1>Chapter One</h1>");
            Assert.Single(result);
            Assert.Equal("Chapter One", result[0].Text);
        }

        [Fact]
        public void ParseHtml_BrInsideParagraph_ReplacedWithSpace_NoDuplicateParagraph()
        {
            var result = EpubFileReader.ParseHtml("<p>Hello<br>World</p>");
            Assert.Single(result);
            Assert.Equal("Hello World", result[0].Text);
        }

        [Theory]
        [InlineData("<p>&amp;</p>", "&")]
        [InlineData("<p>&lt;</p>", "<")]
        [InlineData("<p>&gt;</p>", ">")]
        [InlineData("<p>&mdash;</p>", "—")]
        [InlineData("<p>&ndash;</p>", "–")]
        [InlineData("<p>&ldquo;</p>", "“")]
        [InlineData("<p>&rdquo;</p>", "”")]
        [InlineData("<p>&lsquo;</p>", "‘")]
        [InlineData("<p>&rsquo;</p>", "’")]
        public void ParseHtml_HtmlEntities_DecodedCorrectly(string html, string expected)
        {
            var result = EpubFileReader.ParseHtml(html);
            Assert.Single(result);
            Assert.Equal(expected, result[0].Text);
        }

        [Fact]
        public void ParseHtml_NbspOnlyParagraph_Skipped()
        {
            // &nbsp; decodes to a space, which is whitespace-only, so the paragraph is dropped
            var result = EpubFileReader.ParseHtml("<p>&nbsp;</p>");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseHtml_NbspMixedWithText_DecodedToSpace()
        {
            var result = EpubFileReader.ParseHtml("<p>Hello&nbsp;World</p>");
            Assert.Single(result);
            Assert.Equal("Hello World", result[0].Text);
        }

        [Fact]
        public void ParseHtml_HeadStripped_OnlyBodyContentReturned()
        {
            var html = "<head><title>Title</title></head><body><p>Content</p></body>";
            var result = EpubFileReader.ParseHtml(html);
            Assert.Single(result);
            Assert.Equal("Content", result[0].Text);
        }

        [Fact]
        public void ParseHtml_FigureDiv_Stripped()
        {
            var html = "<div role=\"figure\"><p>Caption</p></div><p>Real</p>";
            var result = EpubFileReader.ParseHtml(html);
            Assert.Single(result);
            Assert.Equal("Real", result[0].Text);
        }

        [Fact]
        public void ParseHtml_CaptionSpan_Removed()
        {
            var html = "<p>Text <span class=\"caption\">cap</span> more</p>";
            var result = EpubFileReader.ParseHtml(html);
            Assert.Single(result);
            Assert.Equal("Text more", result[0].Text);
        }

        [Fact]
        public void ParseHtml_InlineTags_Stripped()
        {
            var result = EpubFileReader.ParseHtml("<p><strong>Bold</strong> text</p>");
            Assert.Single(result);
            Assert.Equal("Bold text", result[0].Text);
        }

        [Fact]
        public void ParseHtml_ExtraWhitespace_Collapsed()
        {
            var result = EpubFileReader.ParseHtml("<p>Hello   World</p>");
            Assert.Single(result);
            Assert.Equal("Hello World", result[0].Text);
        }

        [Fact]
        public void ParseHtml_MultipleConsecutiveBlockElements_AllProduceParagraphs()
        {
            var html = "<h1>Title</h1><p>Para 1</p><p>Para 2</p><h2>Section</h2>";
            var result = EpubFileReader.ParseHtml(html);
            Assert.Equal(4, result.Count);
            Assert.Equal("Title", result[0].Text);
            Assert.Equal("Para 1", result[1].Text);
            Assert.Equal("Para 2", result[2].Text);
            Assert.Equal("Section", result[3].Text);
        }

        [Fact]
        public void ParseHtml_Blockquote_ReturnsParagraph()
        {
            var result = EpubFileReader.ParseHtml("<blockquote>Quote</blockquote>");
            Assert.Single(result);
            Assert.Equal("Quote", result[0].Text);
        }

        // ---------------------------------------------------------------
        // ExtractHtmlTitle
        // ---------------------------------------------------------------

        [Fact]
        public void ExtractHtmlTitle_ReturnsTitle()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<title>My Book</title>");
            Assert.Equal("My Book", result);
        }

        [Fact]
        public void ExtractHtmlTitle_CaseInsensitive()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<TITLE>My Book</TITLE>");
            Assert.Equal("My Book", result);
        }

        [Fact]
        public void ExtractHtmlTitle_NoTitleTag_ReturnsNull()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<p>No title here</p>");
            Assert.Null(result);
        }

        [Fact]
        public void ExtractHtmlTitle_EmptyTitle_ReturnsNull()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<title></title>");
            Assert.Null(result);
        }

        [Fact]
        public void ExtractHtmlTitle_WhitespaceOnlyTitle_ReturnsNull()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<title>   </title>");
            Assert.Null(result);
        }

        [Fact]
        public void ExtractHtmlTitle_TitleWithInnerTags_ReturnsPlainText()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<title><em>Fancy Title</em></title>");
            Assert.Equal("Fancy Title", result);
        }

        [Fact]
        public void ExtractHtmlTitle_TitleWithEntities_DecodesEntities()
        {
            var result = EpubFileReader.ExtractHtmlTitle("<title>Hello &amp; World</title>");
            Assert.Equal("Hello & World", result);
        }
    }
}
