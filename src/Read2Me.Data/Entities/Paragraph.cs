namespace Read2Me.Data.Entities
{
    public class Paragraph
    {
        public Guid Id { get; set; }
        public Guid ChapterId { get; set; }
        public string Order { get; set; } = string.Empty;
        public Guid? CharacterId { get; set; }

        public Chapter Chapter { get; set; } = null!;
        public Character? Character { get; set; }
        public ICollection<ParagraphItem> Items { get; set; } = [];
    }
}
