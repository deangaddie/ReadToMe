namespace Read2Me.Data.Entities
{
    public class Chapter : IBookEntity
    {
        public Guid Id { get; set; }
        public Guid PartId { get; set; }
        public string? Title { get; set; }
        public string Order { get; set; } = string.Empty;

        public Part Part { get; set; } = null!;
        public ICollection<Paragraph> Paragraphs { get; set; } = [];
    }
}
