namespace Read2Me.Data.Entities
{
    public class Voice
    {
        public Guid Id { get; set; }
        public Guid CharacterId { get; set; }
        public string Title { get; set; } = string.Empty;

        public Character Character { get; set; } = null!;
    }
}
