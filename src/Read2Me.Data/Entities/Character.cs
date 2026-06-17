namespace Read2Me.Data.Entities
{
    public class Character
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsNarrator { get; set; }

        public ICollection<Voice> Voices { get; set; } = [];
        public ICollection<CharacterAlias> Aliases { get; set; } = [];
    }
}
