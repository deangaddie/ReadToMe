using Read2Me.Core.Models;

namespace Read2Me.App.Shared;

public sealed class ManualRereadForm
{
    public bool HasVolumes { get; set; }
    public bool HasParts { get; set; }

    public SplitDetectionMode VolumeMode { get; set; } = SplitDetectionMode.Prefix;
    public string VolumePrefix { get; set; } = "";

    public SplitDetectionMode PartMode { get; set; } = SplitDetectionMode.Prefix;
    public string PartPrefix { get; set; } = "";

    public SplitDetectionMode ChapterMode { get; set; } = SplitDetectionMode.Prefix;
    public string ChapterPrefix { get; set; } = "";

    public string? Validate()
    {
        if (HasVolumes && VolumeMode == SplitDetectionMode.Prefix && string.IsNullOrWhiteSpace(VolumePrefix))
            return "Volume prefix cannot be empty.";
        if (HasParts && PartMode == SplitDetectionMode.Prefix && string.IsNullOrWhiteSpace(PartPrefix))
            return "Part prefix cannot be empty.";
        if (ChapterMode == SplitDetectionMode.Prefix && string.IsNullOrWhiteSpace(ChapterPrefix))
            return "Chapter prefix cannot be empty.";
        return null;
    }

    public ManualReadOptions BuildOptions() => new(
        HasVolumes: HasVolumes,
        HasParts: HasParts,
        VolumeRule: HasVolumes
            ? new SectionSplitRule(VolumeMode, VolumeMode == SplitDetectionMode.Prefix ? VolumePrefix.Trim() : null)
            : null,
        PartRule: HasParts
            ? new SectionSplitRule(PartMode, PartMode == SplitDetectionMode.Prefix ? PartPrefix.Trim() : null)
            : null,
        ChapterRule: new SectionSplitRule(ChapterMode, ChapterMode == SplitDetectionMode.Prefix ? ChapterPrefix.Trim() : null)
    );
}
