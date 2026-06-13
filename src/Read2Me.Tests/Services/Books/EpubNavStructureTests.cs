using Read2Me.Core.Models;
using Read2Me.Services.Books;
using VersOne.Epub;
using Xunit;

namespace Read2Me.Tests.Services.Books;

public class EpubNavStructureTests
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static EpubNavigationItem LeafItem(string title, string filePath) =>
        new(EpubNavigationItemType.LINK, title,
            new EpubNavigationItemLink(filePath, string.Empty),
            null, []);

    private static EpubNavigationItem GroupItem(string title, List<EpubNavigationItem> children) =>
        new(EpubNavigationItemType.HEADER, title, null, null, children);

    private static Dictionary<string, ChapterContent> Content(params string[] filePaths) =>
        filePaths.ToDictionary(
            p => p,
            p => new ChapterContent(p, [new ParagraphContent("text")]));

    private static readonly IReadOnlyList<EpubLocalTextContentFile> NoReadingOrder = [];

    // ---------------------------------------------------------------
    // TryBuildFromNav — flat nav (no children)
    // ---------------------------------------------------------------

    [Fact]
    public void FlatNav_AllLeaves_ReturnsNull()
    {
        var nav = new List<EpubNavigationItem>
        {
            LeafItem("Chapter 1", "ch1.html"),
            LeafItem("Chapter 2", "ch2.html"),
        };
        var content = Content("ch1.html", "ch2.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "My Book", NoReadingOrder);

        Assert.Null(result);
    }

    [Fact]
    public void EmptyNav_ReturnsNull()
    {
        var result = EpubFileReader.TryBuildFromNav([], new Dictionary<string, ChapterContent>(), "My Book", NoReadingOrder);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // TryBuildFromNav — 2-level nav (top=part, child=chapter)
    // ---------------------------------------------------------------

    [Fact]
    public void TwoLevelNav_ReturnsSingleVolume()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Part I", [LeafItem("Chapter 1", "ch1.html"), LeafItem("Chapter 2", "ch2.html")]),
            GroupItem("Part II", [LeafItem("Chapter 3", "ch3.html")]),
        };
        var content = Content("ch1.html", "ch2.html", "ch3.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "My Book", NoReadingOrder);

        Assert.NotNull(result);
        Assert.Single(result.Volumes);
    }

    [Fact]
    public void TwoLevelNav_VolumeUsesBookTitle()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Book I", [LeafItem("Chapter 1", "ch1.html")]),
        };
        var content = Content("ch1.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Magician", NoReadingOrder);

        Assert.Equal("Magician", result!.Volumes[0].Title);
    }

    [Fact]
    public void TwoLevelNav_TopItemsBecomeParts()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Foreword", [LeafItem("ch0.html", "ch0.html")]),
            GroupItem("Book I - Pug And Tomas", [LeafItem("ch1.html", "ch1.html"), LeafItem("ch2.html", "ch2.html")]),
            GroupItem("Book II - Milamber", [LeafItem("ch3.html", "ch3.html")]),
        };
        var content = Content("ch0.html", "ch1.html", "ch2.html", "ch3.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Magician", NoReadingOrder);

        Assert.NotNull(result);
        Assert.Equal(3, result.Volumes[0].Parts.Count);
        Assert.Equal("Foreword", result.Volumes[0].Parts[0].Title);
        Assert.Equal("Book I - Pug And Tomas", result.Volumes[0].Parts[1].Title);
        Assert.Equal("Book II - Milamber", result.Volumes[0].Parts[2].Title);
    }

    [Fact]
    public void TwoLevelNav_ChaptersResolvedFromContent()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Part I", [LeafItem("Chapter 1", "ch1.html"), LeafItem("Chapter 2", "ch2.html")]),
        };
        var content = Content("ch1.html", "ch2.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Book", NoReadingOrder);

        var chapters = result!.Volumes[0].Parts[0].Chapters;
        Assert.Equal(2, chapters.Count);
    }

    [Fact]
    public void TwoLevelNav_MixedTopItems_OnlyParentsGetChildren()
    {
        // Some top-level items have children, some don't
        var nav = new List<EpubNavigationItem>
        {
            LeafItem("Title Page", "title.html"),
            GroupItem("Part I", [LeafItem("Chapter 1", "ch1.html")]),
        };
        var content = Content("title.html", "ch1.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Book", NoReadingOrder);

        Assert.NotNull(result);
        Assert.Single(result.Volumes);
        Assert.Equal(2, result.Volumes[0].Parts.Count);
    }

    // ---------------------------------------------------------------
    // TryBuildFromNav — 3-level nav (top=volume, mid=part, bottom=chapter)
    // ---------------------------------------------------------------

    [Fact]
    public void ThreeLevelNav_ReturnsMultipleVolumes()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Volume 1", [
                GroupItem("Part I", [LeafItem("Chapter 1", "ch1.html")]),
                GroupItem("Part II", [LeafItem("Chapter 2", "ch2.html")]),
            ]),
            GroupItem("Volume 2", [
                GroupItem("Part III", [LeafItem("Chapter 3", "ch3.html")]),
            ]),
        };
        var content = Content("ch1.html", "ch2.html", "ch3.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Series", NoReadingOrder);

        Assert.NotNull(result);
        Assert.Equal(2, result.Volumes.Count);
    }

    [Fact]
    public void ThreeLevelNav_VolumeTitlesFromTopLevelItems()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Volume One", [
                GroupItem("Part A", [LeafItem("ch1.html", "ch1.html")]),
            ]),
            GroupItem("Volume Two", [
                GroupItem("Part B", [LeafItem("ch2.html", "ch2.html")]),
            ]),
        };
        var content = Content("ch1.html", "ch2.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Series", NoReadingOrder);

        Assert.Equal("Volume One", result!.Volumes[0].Title);
        Assert.Equal("Volume Two", result.Volumes[1].Title);
    }

    [Fact]
    public void ThreeLevelNav_PartsNestedUnderCorrectVolume()
    {
        var nav = new List<EpubNavigationItem>
        {
            GroupItem("Vol 1", [
                GroupItem("Part A", [LeafItem("ch1.html", "ch1.html")]),
                GroupItem("Part B", [LeafItem("ch2.html", "ch2.html")]),
            ]),
        };
        var content = Content("ch1.html", "ch2.html");

        var result = EpubFileReader.TryBuildFromNav(nav, content, "Book", NoReadingOrder);

        Assert.Equal(2, result!.Volumes[0].Parts.Count);
        Assert.Equal("Part A", result.Volumes[0].Parts[0].Title);
        Assert.Equal("Part B", result.Volumes[0].Parts[1].Title);
    }
}
