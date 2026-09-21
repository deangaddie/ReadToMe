using System.Text.Json.Serialization;

namespace Read2Me.AppData.Entities;

public class ToSentenceCaseConfig
{
    public int Id { get; set; }
    public int ParagraphTtsServiceConfigId { get; set; }
    // The API serialises the config with its children; the way back up would be a cycle.
    [JsonIgnore]
    public ParagraphTtsServiceConfig Config { get; set; } = null!;
    public bool ParagraphEnabled { get; set; }
    public bool WordEnabled { get; set; }
    public int WordMinLength { get; set; }
}
