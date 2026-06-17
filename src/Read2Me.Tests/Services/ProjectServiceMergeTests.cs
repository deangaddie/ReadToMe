using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.IO;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectServiceMergeTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectService _writer;
        private readonly BookCommandHandler _svc;

        public ProjectServiceMergeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Read2MeMergeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = _tempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _writer = new ProjectService(fs, session, NullLogger<ProjectService>.Instance);
            _svc = new BookCommandHandler(session, fs);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private static async Task<ProjectDbContext> OpenDbAsync(string folderPath)
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(folderPath, "project.db")};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }

        private async Task<string> CreateProjectAsync(string name)
        {
            return await _writer.CreateProjectAsync(name, "T", "A", "f.txt",
                new MemoryStream(new byte[] { 1 }), BookFileType.Text);
        }

        // ---------------------------------------------------------------
        // Helpers: seed a full hierarchy
        // ---------------------------------------------------------------

        private record SeedIds(Guid Vol1, Guid Vol2, Guid Part1, Guid Part2,
            Guid Ch1, Guid Ch2, Guid Para1, Guid Para2, Guid Item1, Guid Item2);

        private async Task<SeedIds> SeedTwoOfEachAsync(string folderPath)
        {
            var ids = new SeedIds(
                Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid());

            await using var db = await OpenDbAsync(folderPath);
            string vk1 = Key(), vk2 = Key(vk1);
            db.Volumes.AddRange(
                new Volume { Id = ids.Vol1, Title = "V1", Order = vk1 },
                new Volume { Id = ids.Vol2, Title = "V2", Order = vk2 });

            string pk1 = Key(), pk2 = Key(pk1);
            db.Parts.AddRange(
                new Part { Id = ids.Part1, VolumeId = ids.Vol1, Title = "P1", Order = pk1 },
                new Part { Id = ids.Part2, VolumeId = ids.Vol1, Title = "P2", Order = pk2 });

            string ck1 = Key(), ck2 = Key(ck1);
            db.Chapters.AddRange(
                new Chapter { Id = ids.Ch1, PartId = ids.Part1, Title = "C1", Order = ck1 },
                new Chapter { Id = ids.Ch2, PartId = ids.Part1, Title = "C2", Order = ck2 });

            string pgk1 = Key(), pgk2 = Key(pgk1);
            db.Paragraphs.AddRange(
                new Paragraph { Id = ids.Para1, ChapterId = ids.Ch1, Order = pgk1 },
                new Paragraph { Id = ids.Para2, ChapterId = ids.Ch1, Order = pgk2 });

            string ik1 = Key(), ik2 = Key(ik1);
            db.ParagraphItems.AddRange(
                new ParagraphItem { Id = ids.Item1, ParagraphId = ids.Para1, Text = "hello", Order = ik1 },
                new ParagraphItem { Id = ids.Item2, ParagraphId = ids.Para1, Text = "world", Order = ik2 });

            await db.SaveChangesAsync();
            return ids;
        }

        // ---------------------------------------------------------------
        // MergeVolumeWithPreviousAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeVolumeWithPrevious_MovesChildrenToPrev_DeletesSelf()
        {
            var folder = await CreateProjectAsync("MergeVolPrev");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            // Vol2 has no parts in seed — add one
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Parts.Add(new Part { Id = Guid.NewGuid(), VolumeId = ids.Vol2, Title = "P3", Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeVolumeCommand(folder, ids.Vol2, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Volumes.CountAsync());
            var remainingVol = await verify.Volumes.SingleAsync();
            Assert.Equal(ids.Vol1, remainingVol.Id);
            var parts = await verify.Parts.Where(p => p.VolumeId == ids.Vol1).ToListAsync();
            Assert.Equal(3, parts.Count); // P1, P2 (original) + P3 (moved)
        }

        [Fact]
        public async Task MergeVolumeWithPrevious_WhenFirst_NoOp()
        {
            var folder = await CreateProjectAsync("MergeVolPrevFirst");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeVolumeCommand(folder, ids.Vol1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Volumes.CountAsync());
        }

        [Fact]
        public async Task MergeVolumeWithPrevious_WhenNotFound_NoOp()
        {
            var folder = await CreateProjectAsync("MergeVolPrevNF");
            var ex = await Record.ExceptionAsync(() => _svc.ExecuteAsync(new MergeVolumeCommand(folder, Guid.NewGuid(), MergeDirection.Previous)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // MergeVolumeWithNextAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeVolumeWithNext_MovesNextChildrenToSelf_DeletesNext()
        {
            var folder = await CreateProjectAsync("MergeVolNext");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Parts.Add(new Part { Id = Guid.NewGuid(), VolumeId = ids.Vol2, Title = "P3", Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeVolumeCommand(folder, ids.Vol1, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Volumes.CountAsync());
            Assert.Equal(ids.Vol1, (await verify.Volumes.SingleAsync()).Id);
            Assert.Equal(3, await verify.Parts.Where(p => p.VolumeId == ids.Vol1).CountAsync());
        }

        [Fact]
        public async Task MergeVolumeWithNext_WhenLast_NoOp()
        {
            var folder = await CreateProjectAsync("MergeVolNextLast");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeVolumeCommand(folder, ids.Vol2, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Volumes.CountAsync());
        }

        // ---------------------------------------------------------------
        // MergePartWithPreviousAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergePartWithPrevious_MovesChildrenToPrev_DeletesSelf()
        {
            var folder = await CreateProjectAsync("MergePartPrev");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            // Add chapter to Part2
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), PartId = ids.Part2, Title = "C3", Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergePartCommand(folder, ids.Part2, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Parts.CountAsync());
            Assert.Equal(ids.Part1, (await verify.Parts.SingleAsync()).Id);
            Assert.Equal(3, await verify.Chapters.Where(c => c.PartId == ids.Part1).CountAsync());
        }

        [Fact]
        public async Task MergePartWithPrevious_WhenFirst_NoOp()
        {
            var folder = await CreateProjectAsync("MergePartPrevFirst");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergePartCommand(folder, ids.Part1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Parts.CountAsync());
        }

        [Fact]
        public async Task MergePartWithPrevious_WhenNotFound_NoOp()
        {
            var folder = await CreateProjectAsync("MergePartPrevNF");
            var ex = await Record.ExceptionAsync(() => _svc.ExecuteAsync(new MergePartCommand(folder, Guid.NewGuid(), MergeDirection.Previous)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // MergePartWithNextAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergePartWithNext_MovesNextChildrenToSelf_DeletesNext()
        {
            var folder = await CreateProjectAsync("MergePartNext");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), PartId = ids.Part2, Title = "C3", Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergePartCommand(folder, ids.Part1, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Parts.CountAsync());
            Assert.Equal(ids.Part1, (await verify.Parts.SingleAsync()).Id);
            Assert.Equal(3, await verify.Chapters.Where(c => c.PartId == ids.Part1).CountAsync());
        }

        [Fact]
        public async Task MergePartWithNext_WhenLast_NoOp()
        {
            var folder = await CreateProjectAsync("MergePartNextLast");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergePartCommand(folder, ids.Part2, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Parts.CountAsync());
        }

        // ---------------------------------------------------------------
        // MergeChapterWithPreviousAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeChapterWithPrevious_MovesChildrenToPrev_DeletesSelf()
        {
            var folder = await CreateProjectAsync("MergeChPrev");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            // Add paragraph to Ch2
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Paragraphs.Add(new Paragraph { Id = Guid.NewGuid(), ChapterId = ids.Ch2, Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeChapterCommand(folder, ids.Ch2, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Chapters.CountAsync());
            Assert.Equal(ids.Ch1, (await verify.Chapters.SingleAsync()).Id);
            Assert.Equal(3, await verify.Paragraphs.Where(p => p.ChapterId == ids.Ch1).CountAsync());
        }

        [Fact]
        public async Task MergeChapterWithPrevious_WhenFirst_NoOp()
        {
            var folder = await CreateProjectAsync("MergeChPrevFirst");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeChapterCommand(folder, ids.Ch1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Chapters.CountAsync());
        }

        [Fact]
        public async Task MergeChapterWithPrevious_WhenNotFound_NoOp()
        {
            var folder = await CreateProjectAsync("MergeChPrevNF");
            var ex = await Record.ExceptionAsync(() => _svc.ExecuteAsync(new MergeChapterCommand(folder, Guid.NewGuid(), MergeDirection.Previous)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // MergeChapterWithNextAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeChapterWithNext_MovesNextChildrenToSelf_DeletesNext()
        {
            var folder = await CreateProjectAsync("MergeChNext");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Paragraphs.Add(new Paragraph { Id = Guid.NewGuid(), ChapterId = ids.Ch2, Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeChapterCommand(folder, ids.Ch1, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Chapters.CountAsync());
            Assert.Equal(ids.Ch1, (await verify.Chapters.SingleAsync()).Id);
            Assert.Equal(3, await verify.Paragraphs.Where(p => p.ChapterId == ids.Ch1).CountAsync());
        }

        [Fact]
        public async Task MergeChapterWithNext_WhenLast_NoOp()
        {
            var folder = await CreateProjectAsync("MergeChNextLast");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeChapterCommand(folder, ids.Ch2, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Chapters.CountAsync());
        }

        // ---------------------------------------------------------------
        // MergeParagraphWithPreviousAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeParagraphWithPrevious_MovesItemsToPrev_DeletesSelf()
        {
            var folder = await CreateProjectAsync("MergeParaPrev");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            // Add item to Para2
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.ParagraphItems.Add(new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = ids.Para2, Text = "extra", Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeParagraphCommand(folder, ids.Para2, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Paragraphs.CountAsync());
            Assert.Equal(ids.Para1, (await verify.Paragraphs.SingleAsync()).Id);
            Assert.Equal(3, await verify.ParagraphItems.Where(i => i.ParagraphId == ids.Para1).CountAsync());
        }

        [Fact]
        public async Task MergeParagraphWithPrevious_WhenFirst_NoOp()
        {
            var folder = await CreateProjectAsync("MergeParaPrevFirst");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeParagraphCommand(folder, ids.Para1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Paragraphs.CountAsync());
        }

        [Fact]
        public async Task MergeParagraphWithPrevious_WhenNotFound_NoOp()
        {
            var folder = await CreateProjectAsync("MergeParaPrevNF");
            var ex = await Record.ExceptionAsync(() => _svc.ExecuteAsync(new MergeParagraphCommand(folder, Guid.NewGuid(), MergeDirection.Previous)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // MergeParagraphWithNextAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeParagraphWithNext_MovesNextItemsToSelf_DeletesNext()
        {
            var folder = await CreateProjectAsync("MergeParaNext");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await using (var db = await OpenDbAsync(folderPath))
            {
                db.ParagraphItems.Add(new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = ids.Para2, Text = "extra", Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeParagraphCommand(folder, ids.Para1, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Paragraphs.CountAsync());
            Assert.Equal(ids.Para1, (await verify.Paragraphs.SingleAsync()).Id);
            Assert.Equal(3, await verify.ParagraphItems.Where(i => i.ParagraphId == ids.Para1).CountAsync());
        }

        [Fact]
        public async Task MergeParagraphWithNext_WhenLast_NoOp()
        {
            var folder = await CreateProjectAsync("MergeParaNextLast");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeParagraphCommand(folder, ids.Para2, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Paragraphs.CountAsync());
        }

        // ---------------------------------------------------------------
        // MergeParagraphItemWithPreviousAsync (text concat)
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeParagraphItemWithPrevious_ConcatsText_DeletesSelf()
        {
            var folder = await CreateProjectAsync("MergeItemPrev");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeParagraphItemCommand(folder, ids.Item2, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.ParagraphItems.CountAsync());
            var item = await verify.ParagraphItems.SingleAsync();
            Assert.Equal(ids.Item1, item.Id);
            Assert.Equal("hello world", item.Text);
        }

        [Fact]
        public async Task MergeParagraphItemWithPrevious_WhenFirst_NoOp()
        {
            var folder = await CreateProjectAsync("MergeItemPrevFirst");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeParagraphItemCommand(folder, ids.Item1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.ParagraphItems.CountAsync());
        }

        [Fact]
        public async Task MergeParagraphItemWithPrevious_WhenNotFound_NoOp()
        {
            var folder = await CreateProjectAsync("MergeItemPrevNF");
            var ex = await Record.ExceptionAsync(() => _svc.ExecuteAsync(new MergeParagraphItemCommand(folder, Guid.NewGuid(), MergeDirection.Previous)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // MergeParagraphItemWithNextAsync (text concat)
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeParagraphItemWithNext_ConcatsText_DeletesNext()
        {
            var folder = await CreateProjectAsync("MergeItemNext");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeParagraphItemCommand(folder, ids.Item1, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.ParagraphItems.CountAsync());
            var item = await verify.ParagraphItems.SingleAsync();
            Assert.Equal(ids.Item1, item.Id);
            Assert.Equal("hello world", item.Text);
        }

        [Fact]
        public async Task MergeParagraphItemWithNext_WhenLast_NoOp()
        {
            var folder = await CreateProjectAsync("MergeItemNextLast");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _svc.ExecuteAsync(new MergeParagraphItemCommand(folder, ids.Item2, MergeDirection.Next));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.ParagraphItems.CountAsync());
        }

        // ---------------------------------------------------------------
        // Empty-children edge case: merge works even when entity has no children
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeChapterWithPrevious_EmptyChapter_StillDeletes()
        {
            var folder = await CreateProjectAsync("MergeChPrevEmpty");
            var folderPath = Path.Combine(_tempDir, folder);

            Guid ch1Id, ch2Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
                db.Volumes.Add(vol);
                var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
                db.Parts.Add(part);
                string ck1 = Key(), ck2 = Key(ck1);
                db.Chapters.AddRange(
                    new Chapter { Id = ch1Id = Guid.NewGuid(), PartId = part.Id, Title = "C1", Order = ck1 },
                    new Chapter { Id = ch2Id = Guid.NewGuid(), PartId = part.Id, Title = "C2", Order = ck2 });
                // Ch2 has no paragraphs
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergeChapterCommand(folder, ch2Id, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Chapters.CountAsync());
            Assert.Equal(ch1Id, (await verify.Chapters.SingleAsync()).Id);
        }

        // ---------------------------------------------------------------
        // Single sibling (only one in parent): merge is a no-op
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergePartWithPrevious_OnlySibling_NoOp()
        {
            var folder = await CreateProjectAsync("MergePartOnlySibling");
            var folderPath = Path.Combine(_tempDir, folder);

            Guid partId;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
                db.Volumes.Add(vol);
                db.Parts.Add(new Part { Id = partId = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() });
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new MergePartCommand(folder, partId, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Parts.CountAsync());
        }
    }
}
