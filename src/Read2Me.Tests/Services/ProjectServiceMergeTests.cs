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
    /// <summary>
    /// One persisted merge per level, plus the not-found refusals, against a real project database.
    /// The shape matrix (direction, first or last sibling, item text) is <see cref="Books.BookHierarchyMergeTests"/>'s.
    /// </summary>
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
        public async Task MergeVolumeWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeVolPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeVolumeMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
        public async Task MergePartWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergePartPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergePartMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
        public async Task MergeChapterWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeChPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeChapterMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
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
        public async Task MergeParagraphWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeParaPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeParagraphMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
        }

        // ---------------------------------------------------------------
        // MergeParagraphItemWithPreviousAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task MergeParagraphItemWithPrevious_WhenNotFound_IsRefused()
        {
            var folder = await CreateProjectAsync("MergeItemPrevNF");
            // A merge naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new MergeParagraphItemMutation(folder, Guid.NewGuid(), MergeDirection.Previous))).Reason);
        }
    }
}
