namespace Read2Me.Data.Entities
{
    public class Paragraph : IBookEntity
    {
        public Guid Id { get; set; }
        public Guid ChapterId { get; set; }
        public string Order { get; set; } = string.Empty;

        public Chapter Chapter { get; set; } = null!;
        public ICollection<ParagraphItem> Items { get; set; } = [];
    }
}
