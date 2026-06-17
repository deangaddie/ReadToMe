namespace Read2Me.Data.Entities
{
    public class CharacterAlias
    {
        public Guid Id { get; set; }
        public Guid CharacterId { get; set; }
        public string Name { get; set; } = string.Empty;

        public Character Character { get; set; } = null!;
    }
}
