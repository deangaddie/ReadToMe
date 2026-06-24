namespace Read2Me.AppData.Entities;

public class TextSubstitutionStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int ParagraphTtsServiceConfigId { get; set; }
    public ParagraphTtsServiceConfig Config { get; set; } = null!;
    public string FromText { get; set; } = "";
    public string ToText { get; set; } = "";
    public int Order { get; set; }
}
