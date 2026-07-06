namespace Read2Me.Data.Entities
{
    public class Part : IBookEntity
    {
        public Guid Id { get; set; }
        public Guid VolumeId { get; set; }
        public string? Title { get; set; }
        public string Order { get; set; } = string.Empty;

        public Volume Volume { get; set; } = null!;
        public ICollection<Chapter> Chapters { get; set; } = [];
    }
}
