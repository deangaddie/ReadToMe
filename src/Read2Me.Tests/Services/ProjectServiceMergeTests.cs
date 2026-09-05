using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectServiceMergeTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectService _writer;
        private readonly BookMutations _mutations;

        public ProjectServiceMergeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Read2MeMergeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = _tempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<ProjectService>();
            services.AddScoped(sp => NullLogger<ProjectService>.Instance);
            var sp = services.BuildServiceProvider();

            _writer = sp.GetRequiredService<ProjectService>();
            _mutations = sp.GetRequiredService<BookMutations>();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private static async Task<ProjectDbContext> OpenDbAsync(string folderPath)
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(folderPath, "project.db")};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }

        private BookHierarchyBuilder BuilderFor(string folderPath) =>
            new BookHierarchyBuilder(() => OpenDbAsync(folderPath));

        private async Task<string> CreateProjectAsync(string name)
        {
            return await _writer.CreateProjectAsync(name, "T", "A", "f.txt",
                new MemoryStream(new byte[] { 1 }), BookFileType.Text);
        }

        // ---------------------------------------------------------------
        // Helpers: seed a full hierarchy
        // ---------------------------------------------------------------

        // Seeds: Vol1 > Part1 > Ch1 > Para1 + Para2 (each with 1 item)
        //        Vol1 > Part2 (no children)
        //        Vol2 (no children)
        private record SeedIds(Guid Vol1, Guid Vol2, Guid Part1, Guid Part2,
            Guid Ch1, Guid Ch2, Guid Para1, Guid Para2, Guid Item1, Guid Item2);

        private async Task<SeedIds> SeedTwoOfEachAsync(string folderPath)
        {
            var b = BuilderFor(folderPath);
            await b
                .AddVolume("vol1", v => v
                    .AddPart("part1", p => p
                        .AddChapter("ch1", c => c
                            .AddParagraph("para1", para => para
                                .AddNarration("item1", "hello")
                                .AddNarration("item2", "world"))
                            .AddParagraph("para2"))
                        .AddChapter("ch2"))
                    .AddPart("part2"))
                .AddVolume("vol2")
                .AddHierarchyAsync();

            return new SeedIds(
                Vol1: b.VolumeId("vol1"), Vol2: b.VolumeId("vol2"),
                Part1: b.PartId("part1"), Part2: b.PartId("part2"),
                Ch1: b.ChapterId("ch1"), Ch2: b.ChapterId("ch2"),
                Para1: b.ParagraphId("para1"), Para2: b.ParagraphId("para2"),
                Item1: b.ItemId("item1"), Item2: b.ItemId("item2"));
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
                db.Parts.Add(new Data.Entities.Part { Id = Guid.NewGuid(), VolumeId = ids.Vol2, Title = "P3", Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergeVolumeMutation(folder, ids.Vol2, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Volumes.CountAsync());
            var remainingVol = await verify.Volumes.SingleAsync();
            Assert.Equal(ids.Vol1, remainingVol.Id);
            var parts = await verify.Parts.Where(p => p.VolumeId == ids.Vol1).ToListAsync();
            Assert.Equal(3, parts.Count); // part1, part2 (original) + P3 (moved)
        }

        [Fact]
        public async Task MergeVolumeWithPrevious_WhenFirst_NoOp()
        {
            var folder = await CreateProjectAsync("MergeVolPrevFirst");
            var folderPath = Path.Combine(_tempDir, folder);
            var ids = await SeedTwoOfEachAsync(folderPath);

            await _mutations.CommitAsync(new MergeVolumeMutation(folder, ids.Vol1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Volumes.CountAsync());
        }

        [Fact]
        public async Task MergeVolumeWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeVolPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeVolumeMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
                db.Parts.Add(new Data.Entities.Part { Id = Guid.NewGuid(), VolumeId = ids.Vol2, Title = "P3", Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergeVolumeMutation(folder, ids.Vol1, MergeDirection.Next));

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

            await _mutations.CommitAsync(new MergeVolumeMutation(folder, ids.Vol2, MergeDirection.Next));

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
                db.Chapters.Add(new Data.Entities.Chapter { Id = Guid.NewGuid(), PartId = ids.Part2, Title = "C3", Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergePartMutation(folder, ids.Part2, MergeDirection.Previous));

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

            await _mutations.CommitAsync(new MergePartMutation(folder, ids.Part1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Parts.CountAsync());
        }

        [Fact]
        public async Task MergePartWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergePartPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergePartMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
                db.Chapters.Add(new Data.Entities.Chapter { Id = Guid.NewGuid(), PartId = ids.Part2, Title = "C3", Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergePartMutation(folder, ids.Part1, MergeDirection.Next));

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

            await _mutations.CommitAsync(new MergePartMutation(folder, ids.Part2, MergeDirection.Next));

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
                db.Paragraphs.Add(new Data.Entities.Paragraph { Id = Guid.NewGuid(), ChapterId = ids.Ch2, Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergeChapterMutation(folder, ids.Ch2, MergeDirection.Previous));

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

            await _mutations.CommitAsync(new MergeChapterMutation(folder, ids.Ch1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Chapters.CountAsync());
        }

        [Fact]
        public async Task MergeChapterWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeChPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeChapterMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
                db.Paragraphs.Add(new Data.Entities.Paragraph { Id = Guid.NewGuid(), ChapterId = ids.Ch2, Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergeChapterMutation(folder, ids.Ch1, MergeDirection.Next));

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

            await _mutations.CommitAsync(new MergeChapterMutation(folder, ids.Ch2, MergeDirection.Next));

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
                db.ParagraphItems.Add(new Data.Entities.ParagraphItem { Id = Guid.NewGuid(), ParagraphId = ids.Para2, Text = "extra", Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergeParagraphMutation(folder, ids.Para2, MergeDirection.Previous));

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

            await _mutations.CommitAsync(new MergeParagraphMutation(folder, ids.Para1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.Paragraphs.CountAsync());
        }

        [Fact]
        public async Task MergeParagraphWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeParaPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeParagraphMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
                db.ParagraphItems.Add(new Data.Entities.ParagraphItem { Id = Guid.NewGuid(), ParagraphId = ids.Para2, Text = "extra", Order = "a" });
                await db.SaveChangesAsync();
            }

            await _mutations.CommitAsync(new MergeParagraphMutation(folder, ids.Para1, MergeDirection.Next));

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

            await _mutations.CommitAsync(new MergeParagraphMutation(folder, ids.Para2, MergeDirection.Next));

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

            await _mutations.CommitAsync(new MergeParagraphItemMutation(folder, ids.Item2, MergeDirection.Previous));

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

            await _mutations.CommitAsync(new MergeParagraphItemMutation(folder, ids.Item1, MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(2, await verify.ParagraphItems.CountAsync());
        }

        [Fact]
        public async Task MergeParagraphItemWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeItemPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeParagraphItemMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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

            await _mutations.CommitAsync(new MergeParagraphItemMutation(folder, ids.Item1, MergeDirection.Next));

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

            await _mutations.CommitAsync(new MergeParagraphItemMutation(folder, ids.Item2, MergeDirection.Next));

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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v.AddPart(configure: p => p
                .AddChapter("ch1")
                .AddChapter("ch2")))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new MergeChapterMutation(folder, b.ChapterId("ch2"), MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Chapters.CountAsync());
            Assert.Equal(b.ChapterId("ch1"), (await verify.Chapters.SingleAsync()).Id);
        }

        // ---------------------------------------------------------------
        // Single sibling (only one in parent): merge is a no-op
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergePartWithPrevious_OnlySibling_NoOp()
        {
            var folder = await CreateProjectAsync("MergePartOnlySibling");
            var folderPath = Path.Combine(_tempDir, folder);

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v.AddPart("part"))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new MergePartMutation(folder, b.PartId("part"), MergeDirection.Previous));

            await using var verify = await OpenDbAsync(folderPath);
            Assert.Equal(1, await verify.Parts.CountAsync());
        }
    }
}
