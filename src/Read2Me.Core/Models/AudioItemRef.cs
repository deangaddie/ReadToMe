namespace Read2Me.Core.Models;

public readonly record struct AudioItemRef(Guid ParagraphItemId, Guid ParagraphId, Guid ChapterId, Guid PartId, Guid VolumeId);
