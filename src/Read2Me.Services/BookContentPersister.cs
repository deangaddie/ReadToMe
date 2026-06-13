using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;

namespace Read2Me.Services
{
    public class BookContentPersister : IBookContentPersister
    {
        public async Task PersistAsync(ProjectDbContext db, BookContent content, CancellationToken cancellationToken = default)
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

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
                            var attributed = NarrationClassifier.Classify(segments);
                            var segKeys = GenerateKeys(attributed.Count);
                            for (int si = 0; si < attributed.Count; si++)
                            {
                                var seg = attributed[si];
                                db.ParagraphItems.Add(new ParagraphItem
                                {
                                    Id = Guid.NewGuid(),
                                    ParagraphId = paragraph.Id,
                                    Order = segKeys[si],
                                    ItemType = seg.ItemType,
                                    CharacterId = seg.CharacterId,
                                    Text = seg.Text
                                });
                            }
                        }
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
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
