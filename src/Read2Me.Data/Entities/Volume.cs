namespace Read2Me.Data.Entities
{
    public class Volume
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Order { get; set; } = string.Empty;

        public ICollection<Part> Parts { get; set; } = [];
    }
}
