using Read2Me.App.Shared;
using Read2Me.Core.Models;
using Xunit;

namespace Read2Me.Tests.App.Dialogs;

public class ManualRereadFormTests
{
    [Fact]
    public void Validate_DefaultState_ChapterPrefixError()
    {
        var form = new ManualRereadForm();
        // Default: ChapterMode == Prefix, ChapterPrefix == ""
        Assert.Equal("Chapter prefix cannot be empty.", form.Validate());
    }

    [Fact]
    public void Validate_WithChapterPrefix_ReturnsNull()
    {
        var form = new ManualRereadForm { ChapterPrefix = "Chapter" };
        Assert.Null(form.Validate());
    }

    [Fact]
    public void Validate_VolumesEnabledWithEmptyPrefix_VolumePrefixErrorFirst()
    {
        var form = new ManualRereadForm
        {
            HasVolumes = true,
            VolumeMode = SplitDetectionMode.Prefix,
            VolumePrefix = "",
            ChapterPrefix = "Chapter",
        };
        Assert.Equal("Volume prefix cannot be empty.", form.Validate());
    }

    [Fact]
    public void Validate_PartsEnabledWithEmptyPrefix_PartPrefixError()
    {
        var form = new ManualRereadForm
        {
            HasParts = true,
            PartMode = SplitDetectionMode.Prefix,
            PartPrefix = "",
            ChapterPrefix = "Chapter",
        };
        Assert.Equal("Part prefix cannot be empty.", form.Validate());
    }

    [Fact]
    public void Validate_NonPrefixVolumeMode_NoVolumeError()
    {
        var form = new ManualRereadForm
        {
            HasVolumes = true,
            VolumeMode = SplitDetectionMode.Number,
            ChapterPrefix = "Chapter",
        };
        Assert.Null(form.Validate());
    }

    [Fact]
    public void BuildOptions_WithChapterPrefix_CorrectOptions()
    {
        var form = new ManualRereadForm { ChapterPrefix = "Chapter" };
        var options = form.BuildOptions();

        Assert.False(options.HasVolumes);
        Assert.False(options.HasParts);
        Assert.Null(options.VolumeRule);
        Assert.Null(options.PartRule);
        Assert.Equal(SplitDetectionMode.Prefix, options.ChapterRule.Mode);
        Assert.Equal("Chapter", options.ChapterRule.Prefix);
    }

    [Fact]
    public void BuildOptions_WithVolumesAndParts_AllRulesPresent()
    {
        var form = new ManualRereadForm
        {
            HasVolumes = true,
            VolumePrefix = "Volume",
            HasParts = true,
            PartMode = SplitDetectionMode.Number,
            ChapterPrefix = "Chapter",
        };
        var options = form.BuildOptions();

        Assert.True(options.HasVolumes);
        Assert.Equal("Volume", options.VolumeRule!.Prefix);
        Assert.Equal(SplitDetectionMode.Number, options.PartRule!.Mode);
        Assert.Null(options.PartRule.Prefix);
    }
}

