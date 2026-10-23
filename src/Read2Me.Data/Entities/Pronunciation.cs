namespace Read2Me.Data.Entities
{
    /// <summary>
    /// One entry in the book's Pronunciation list: TTS is asked to say <see cref="SpokenForm"/>
    /// wherever the item text has <see cref="WrittenForm"/>. The book text never changes.
    /// </summary>
    public class Pronunciation
    {
        public Guid Id { get; set; }
        public string WrittenForm { get; set; } = string.Empty;
        public string SpokenForm { get; set; } = string.Empty;
    }
}
