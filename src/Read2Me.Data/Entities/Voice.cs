using Read2Me.Data.Enums;

namespace Read2Me.Data.Entities
{
    public class Voice
    {
        public Guid Id { get; set; }
        public Guid CharacterId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsDefault { get; set; }
        public VoiceSource Source { get; set; }
        public string? DesignPrompt { get; set; }
        public string? Transcript { get; set; }
        public string? AudioFileName { get; set; }
        public string? SettingsOverrideJson { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public Character Character { get; set; } = null!;
    }
}
