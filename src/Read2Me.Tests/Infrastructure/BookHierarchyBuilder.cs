using System;
using System.Threading.Tasks;
using FractionalIndexing;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.Tests.Infrastructure
{
    public sealed class BookHierarchyBuilder
    {
        private readonly ProjectDbContext _db;
        private string? _lastVolumeOrder;

        public BookHierarchyBuilder(ProjectDbContext db) => _db = db;

        public Project AddProject(string title = "Test Book", string author = "Author",
            BookFileType type = BookFileType.Text, string filename = "book.txt")
        {
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Title = title,
                BookTitle = title,
                Author = author,
                Filename = filename,
                Type = type,
            };
            _db.Projects.Add(project);
            return project;
        }

        public Volume AddSimpleVolume(string title, int paragraphs = 1)
        {
            var volOrder = NextVolumeOrder();
            var vol = new Volume { Id = Guid.NewGuid(), Title = title, Order = volOrder };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var chapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            _db.Volumes.Add(vol);
            _db.Parts.Add(part);
            _db.Chapters.Add(chapter);

            string? prev = null;
            for (var i = 0; i < paragraphs; i++)
            {
                var paraOrder = OrderKeyGenerator.GenerateKeyBetween(prev, null);
                prev = paraOrder;
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = paraOrder };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    Order = Key(),
                    ItemType = ParagraphItemType.Narration,
                    Text = $"Paragraph {i + 1}",
                    CharacterId = ProjectDbContext.NarratorId,
                };
                _db.Paragraphs.Add(para);
                _db.ParagraphItems.Add(item);
            }
            return vol;
        }

        public Task SaveAsync() => _db.SaveChangesAsync();

        private string NextVolumeOrder()
        {
            _lastVolumeOrder = OrderKeyGenerator.GenerateKeyBetween(_lastVolumeOrder, null);
            return _lastVolumeOrder;
        }

        private static string Key() => OrderKeyGenerator.GenerateKeyBetween(null, null);
    }
}
