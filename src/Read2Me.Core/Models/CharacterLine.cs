namespace Read2Me.Core.Models;

public readonly record struct CharacterLine(Guid ItemId, Guid ParagraphId, Guid ChapterId, string Text);
