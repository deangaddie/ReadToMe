using System.Text.Json.Serialization;

namespace Read2Me.AppData.Entities;

public class TextSubstitutionStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int ParagraphTtsServiceConfigId { get; set; }
    // The API serialises the config with its children; the way back up would be a cycle.
    [JsonIgnore]
    public ParagraphTtsServiceConfig Config { get; set; } = null!;
    public string FromText { get; set; } = "";
    public string ToText { get; set; } = "";
    public int Order { get; set; }
}
