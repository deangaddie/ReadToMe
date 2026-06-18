using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Models;
using Read2Me.Services.Books;
using VersOne.Epub;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class EpubFileReaderTests
    {
        // ---------------------------------------------------------------
        // BuildAnchorMap
        // ---------------------------------------------------------------

        [Fact]
        public void BuildAnchorMap_EmptyHtml_ReturnsEmpty()
        {
            var map = EpubFileReader.BuildAnchorMap(string.Empty);
            Assert.Empty(map);
        }

        [Fact]
        public void BuildAnchorMap_IdOnBlockTag_MapsToParaIndex()
        {
            // id="sec2" is on the second block tag, which starts paragraph index 1
            var html = "<p>Para 0</p><p id=\"sec2\">Para 1</p>";
            var map = EpubFileReader.BuildAnchorMap(html);
            Assert.True(map.ContainsKey("sec2"));
            Assert.Equal(1, map["sec2"]);
        }

        [Fact]
        public void BuildAnchorMap_IdOnFirstBlock_MapsToZero()
        {
            var html = "<p id=\"start\">First paragraph</p><p>Second</p>";
            var map = EpubFileReader.BuildAnchorMap(html);
            Assert.Equal(0, map["start"]);
        }

        [Fact]
        public void BuildAnchorMap_AnchorNameTag_Recognized()
        {
            var html = "<p><a name=\"ch1\"></a>Content</p>";
            var map = EpubFileReader.BuildAnchorMap(html);
            Assert.True(map.ContainsKey("ch1"));
        }

        [Fact]
        public void BuildAnchorMap_CaseInsensitive()
        {
            var html = "<p ID=\"MyAnchor\">text</p>";
            var map = EpubFileReader.BuildAnchorMap(html);
            Assert.True(map.ContainsKey("MyAnchor"));
        }

        // ---------------------------------------------------------------
        // BuildAnchoredContent
        // ---------------------------------------------------------------

        private static EpubNavigationItem LeafItem(string title, string filePath, string anchor = "") =>
            new(EpubNavigationItemType.LINK, title,
                new EpubNavigationItemLink(filePath, filePath, anchor),
                null, []);

        private static EpubNavigationItem GroupItem(string title, List<EpubNavigationItem> children) =>
            new(EpubNavigationItemType.HEADER, title, null, null, children);

        [Fact]
        public void BuildAnchoredContent_NoAnchors_ContentByPathUnchanged()
        {
            var nav = new List<EpubNavigationItem>
            {
                GroupItem("Part I", [LeafItem("Ch 1", "ch1.html"), LeafItem("Ch 2", "ch2.html")]),
            };
            var contentByPath = new Dictionary<string, ChapterContent>
            {
                ["ch1.html"] = new("Ch 1", [new("Para 1")]),
                ["ch2.html"] = new("Ch 2", [new("Para 2")]),
            };
            var raw = new Dictionary<string, string>
            {
                ["ch1.html"] = "<p>Para 1</p>",
                ["ch2.html"] = "<p>Para 2</p>",
            };

            var result = EpubFileReader.BuildAnchoredContent(nav, contentByPath, raw);

            // Original keys still present, no anchored keys added
            Assert.True(result.ContainsKey("ch1.html"));
            Assert.True(result.ContainsKey("ch2.html"));
            Assert.DoesNotContain(result.Keys, k => k.Contains('#'));
        }

        [Fact]
        public void BuildAnchoredContent_TwoAnchoredChaptersInOneFile_SlicedCorrectly()
        {
            // Single file with two chapters split by anchors
            var html = "<p id=\"ch1\">Chapter 1 text</p><p id=\"ch2\">Chapter 2 text</p>";
            var nav = new List<EpubNavigationItem>
            {
                GroupItem("Book", [
                    LeafItem("Chapter 1", "book.html", "ch1"),
                    LeafItem("Chapter 2", "book.html", "ch2"),
                ]),
            };
            var fullParas = EpubFileReader.ParseHtml(html);
            var contentByPath = new Dictionary<string, ChapterContent>
            {
                ["book.html"] = new("Book", fullParas),
            };
            var raw = new Dictionary<string, string> { ["book.html"] = html };

            var result = EpubFileReader.BuildAnchoredContent(nav, contentByPath, raw);

            Assert.True(result.ContainsKey("book.html#ch1"));
            Assert.True(result.ContainsKey("book.html#ch2"));
            Assert.Single(result["book.html#ch1"].Paragraphs);
            Assert.Equal("Chapter 1 text", result["book.html#ch1"].Paragraphs[0].Text);
            Assert.Single(result["book.html#ch2"].Paragraphs);
            Assert.Equal("Chapter 2 text", result["book.html#ch2"].Paragraphs[0].Text);
        }

        [Fact]
        public void BuildAnchoredContent_MultiParaChapter_SlicesCorrectRange()
        {
            var html = "<p id=\"ch1\">A</p><p>B</p><p id=\"ch2\">C</p>";
            var nav = new List<EpubNavigationItem>
            {
                GroupItem("Book", [
                    LeafItem("Ch 1", "f.html", "ch1"),
                    LeafItem("Ch 2", "f.html", "ch2"),
                ]),
            };
            var contentByPath = new Dictionary<string, ChapterContent>
            {
                ["f.html"] = new("Book", EpubFileReader.ParseHtml(html)),
            };
            var raw = new Dictionary<string, string> { ["f.html"] = html };

            var result = EpubFileReader.BuildAnchoredContent(nav, contentByPath, raw);

            Assert.Equal(2, result["f.html#ch1"].Paragraphs.Count); // A + B
            Assert.Single(result["f.html#ch2"].Paragraphs);          // C
        }

        [Fact]
        public void BuildAnchoredContent_AnchorNotInHtml_SkipsEntry()
        {
            var html = "<p id=\"ch1\">Text</p>";
            var nav = new List<EpubNavigationItem>
            {
                GroupItem("Book", [
                    LeafItem("Ch 1", "f.html", "ch1"),
                    LeafItem("Ch 2", "f.html", "missing-anchor"),
                ]),
            };
            var contentByPath = new Dictionary<string, ChapterContent>
            {
                ["f.html"] = new("Book", EpubFileReader.ParseHtml(html)),
            };
            var raw = new Dictionary<string, string> { ["f.html"] = html };

            var result = EpubFileReader.BuildAnchoredContent(nav, contentByPath, raw);

            Assert.True(result.ContainsKey("f.html#ch1"));
            Assert.False(result.ContainsKey("f.html#missing-anchor"));
            // Full file still accessible
            Assert.True(result.ContainsKey("f.html"));
        }


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
