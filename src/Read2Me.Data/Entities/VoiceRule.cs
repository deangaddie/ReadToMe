using Read2Me.Data.Enums;

namespace Read2Me.Data.Entities
{
    public class VoiceRule
    {
        public Guid Id { get; set; }
        public Guid CharacterId { get; set; }
        public Guid VoiceId { get; set; }
        public string Rank { get; set; } = "";
        public bool IsDefault { get; set; }
        public VoiceAnchorLevel? FromLevel { get; set; }
        public Guid? FromNodeId { get; set; }
        public VoiceAnchorLevel? ToLevel { get; set; }
        public Guid? ToNodeId { get; set; }

        public Character Character { get; set; } = null!;
        public Voice Voice { get; set; } = null!;
    }
}
