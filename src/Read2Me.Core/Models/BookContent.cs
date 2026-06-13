namespace Read2Me.Core.Models
{
    public record BookContent(List<VolumeContent> Volumes);

    public record VolumeContent(string Title, List<PartContent> Parts);

    public record PartContent(string? Title, List<ChapterContent> Chapters);

    public record ChapterContent(string? Title, List<ParagraphContent> Paragraphs);

    public record ParagraphContent(string Text);
}
