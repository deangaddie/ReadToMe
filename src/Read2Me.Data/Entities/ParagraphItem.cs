using Read2Me.Data.Enums;

namespace Read2Me.Data.Entities
{
    public class ParagraphItem
    {
        public Guid Id { get; set; }
        public Guid ParagraphId { get; set; }
        public string Order { get; set; } = string.Empty;
        public ParagraphItemType ItemType { get; set; }
        public string? Text { get; set; }
        public Guid? CharacterId { get; set; }
        public string? VoiceInstructions { get; set; }

        public Paragraph Paragraph { get; set; } = null!;
        public Character? Character { get; set; }
    }
}
