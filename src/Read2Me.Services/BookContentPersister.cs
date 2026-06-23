using Read2Me.Core.Models;
using Read2Me.Core.Utils;
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

            string? prev = null;
            string NextKey() => prev = OrderHelper.GetNextOrder(prev);

            foreach (var vol in content.Volumes)
            {
                var volume = new Volume { Id = Guid.NewGuid(), Title = vol.Title, Order = NextKey() };
                db.Volumes.Add(volume);

                foreach (var part in vol.Parts)
                {
                    var partEntity = new Part { Id = Guid.NewGuid(), VolumeId = volume.Id, Title = part.Title, Order = NextKey() };
                    db.Parts.Add(partEntity);

                    foreach (var ch in part.Chapters)
                    {
                        var chapter = new Chapter { Id = Guid.NewGuid(), PartId = partEntity.Id, Title = ch.Title, Order = NextKey() };
                        db.Chapters.Add(chapter);

                        foreach (var para in ch.Paragraphs)
                        {
                            var paragraph = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = NextKey() };
                            db.Paragraphs.Add(paragraph);

                            var segments = ParagraphSplitter.Split(para.Text);
                            var attributed = NarrationClassifier.Classify(segments, ProjectDbContext.NarratorId);
                            foreach (var seg in attributed)
                            {
                                db.ParagraphItems.Add(new ParagraphItem
                                {
                                    Id = Guid.NewGuid(),
                                    ParagraphId = paragraph.Id,
                                    Order = NextKey(),
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
    }
}
