namespace Read2Me.AppData.Entities;

public class ToSentenceCaseConfig
{
    public int Id { get; set; }
    public int ParagraphTtsServiceConfigId { get; set; }
    public ParagraphTtsServiceConfig Config { get; set; } = null!;
    public bool ParagraphEnabled { get; set; }
    public bool WordEnabled { get; set; }
    public int WordMinLength { get; set; }
}
