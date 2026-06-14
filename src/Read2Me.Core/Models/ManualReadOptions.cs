namespace Read2Me.Core.Models
{
    public record ManualReadOptions(
        bool HasVolumes,
        bool HasParts,
        SectionSplitRule? VolumeRule,
        SectionSplitRule? PartRule,
        SectionSplitRule ChapterRule
    );

    public record SectionSplitRule(
        SplitDetectionMode Mode,
        string? Prefix
    );

    public enum SplitDetectionMode { Prefix, Number, RomanNumeral }
}
