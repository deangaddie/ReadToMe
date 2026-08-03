using Read2Me.Data.Enums;

namespace Read2Me.Data.Entities
{
    public class Project
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string BookTitle { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
        public BookFileType Type { get; set; }
        public string? CoverImage { get; set; }
        public bool NarratorOnlyMode { get; set; }

        /// <summary>
        /// The Character who narrates this book, or null for the seed Narrator voice.
        /// Deliberately no EF relationship and no FK — a dangling link self-heals.
        /// Read it only through <see cref="NarratorIdentity.LoadAsync"/> (ADR-0004).
        /// </summary>
        public Guid? NarratorCharacterId { get; set; }
    }
}
