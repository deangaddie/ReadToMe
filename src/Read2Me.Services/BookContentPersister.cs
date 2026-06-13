using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;

namespace Read2Me.Services
{
    internal static class BookContentPersister
    {
        internal static async Task PersistAsync(ProjectDbContext db, BookContent content, CancellationToken cancellationToken = default)
        {
            var volKeys = GenerateKeys(content.Volumes.Count);
            for (int vi = 0; vi < content.Volumes.Count; vi++)
            {
                var vol = content.Volumes[vi];
                var volume = new Volume { Id = Guid.NewGuid(), Title = vol.Title, Order = volKeys[vi] };
                db.Volumes.Add(volume);

                var partKeys = GenerateKeys(vol.Parts.Count);
                for (int pi = 0; pi < vol.Parts.Count; pi++)
                {
                    var part = vol.Parts[pi];
                    var partEntity = new Part { Id = Guid.NewGuid(), VolumeId = volume.Id, Title = part.Title, Order = partKeys[pi] };
                    db.Parts.Add(partEntity);

                    var chKeys = GenerateKeys(part.Chapters.Count);
                    for (int ci = 0; ci < part.Chapters.Count; ci++)
                    {
                        var ch = part.Chapters[ci];
                        var chapter = new Chapter { Id = Guid.NewGuid(), PartId = partEntity.Id, Title = ch.Title, Order = chKeys[ci] };
                        db.Chapters.Add(chapter);

                        var paraKeys = GenerateKeys(ch.Paragraphs.Count);
                        for (int pri = 0; pri < ch.Paragraphs.Count; pri++)
                        {
                            var paragraph = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = paraKeys[pri] };
                            db.Paragraphs.Add(paragraph);

                            var segments = ParagraphSplitter.Split(ch.Paragraphs[pri].Text);
                            var segKeys = GenerateKeys(segments.Count);
                            for (int si = 0; si < segments.Count; si++)
                            {
                                var seg = segments[si];
                                db.ParagraphItems.Add(new ParagraphItem
                                {
                                    Id = Guid.NewGuid(),
                                    ParagraphId = paragraph.Id,
                                    Order = segKeys[si],
                                    ItemType = seg.Type == SegmentType.Narration ? ParagraphItemType.Narration : ParagraphItemType.Character,
                                    CharacterId = seg.Type == SegmentType.Narration ? ProjectDbContext.NarratorId : null,
                                    Text = seg.Text
                                });
                            }
                        }
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        private static List<string> GenerateKeys(int count)
        {
            var keys = new List<string>(count);
            string? prev = null;
            for (int i = 0; i < count; i++)
            {
                prev = OrderKeyGenerator.GenerateKeyBetween(prev, null);
                keys.Add(prev);
            }
            return keys;
        }
    }
}
