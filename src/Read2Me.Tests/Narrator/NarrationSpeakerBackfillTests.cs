using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Services.NodeStatus;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// The expand step of "narration is a speaker, not an item type": every narration item in every
    /// existing project carries the narrator as its speaker. Nothing reads that yet, so what these
    /// tests assert stays the same matters as much as what they assert changes.
    /// </summary>
    public class NarrationSpeakerBackfillTests : ProjectDbTestBase
    {
        /// <summary>The migration immediately before the backfill.</summary>
        private const string PriorMigration = "20260803101800_AddNarratorCharacterId";

        private static readonly Guid AliceId = new("22222222-2222-2222-2222-222222222222");
        private static readonly Guid VolumeId = new("55555555-5555-5555-5555-555555555555");
        private static readonly Guid PartId = new("66666666-6666-6666-6666-666666666666");
        private static readonly Guid ChapterId = new("33333333-3333-3333-3333-333333333333");
        private static readonly Guid ParagraphId = new("44444444-4444-4444-4444-444444444444");

        // The seeded items, by the role each one plays in the backfill.
        private static readonly Guid NarrationWithSpeaker = new("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid NarrationWithoutSpeaker = new("aaaaaaaa-0000-0000-0000-000000000002");
        private static readonly Guid InsertedTitle = new("aaaaaaaa-0000-0000-0000-000000000003");
        private static readonly Guid DialogAttributed = new("aaaaaaaa-0000-0000-0000-000000000004");
        private static readonly Guid DialogUnattributed = new("aaaaaaaa-0000-0000-0000-000000000005");
        private static readonly Guid PauseItem = new("aaaaaaaa-0000-0000-0000-000000000006");

        private static string Key(string? prev = null) => OrderKeyGenerator.GenerateKeyBetween(prev, null);

        /// <summary>Opens an unmigrated context over the named folder. Caller owns dispose.</summary>
        private ProjectDbContext OpenUnmigratedAt(string folder)
        {
            var path = Path.Combine(TempDir, folder);
            Directory.CreateDirectory(path);
            return new ProjectDbContext(
                new DbContextOptionsBuilder<ProjectDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(path, "project.db")};Pooling=false")
                    .Options);
        }

        /// <summary>
        /// Migrates the named folder to <see cref="PriorMigration"/> and seeds one book by raw SQL:
        /// narration with and without a speaker (the latter standing in for an inserted title),
        /// dialog both attributed and not, and a pause.
        /// </summary>
        private async Task SeedAtPriorMigrationAsync(string folder, bool conforming = false)
        {
            await using var old = OpenUnmigratedAt(folder);
            await old.GetService<IMigrator>().MigrateAsync(PriorMigration);

            var narrator = ProjectDbContext.NarratorId;
            string Speaker(Guid? id) => id is null ? "NULL" : $"'{id}'";
            var unstamped = conforming ? (Guid?)narrator : null;

            // EF1002: every interpolated value here is a test-owned constant, never user input.
#pragma warning disable EF1002
            await old.Database.ExecuteSqlRawAsync(
                $"""
                 INSERT INTO Projects (Id, Title, BookTitle, Author, Filename, Type, NarratorOnlyMode)
                 VALUES ('11111111-1111-1111-1111-111111111111', 'T', 'T', 'A', 'book.txt', 'Text', 0);

                 INSERT INTO Characters (Id, Name, IsNarrator) VALUES ('{AliceId}', 'Alice', 0);

                 INSERT INTO Volumes (Id, Title, "Order") VALUES ('{VolumeId}', 'V', '{Key()}');
                 INSERT INTO Parts (Id, VolumeId, Title, "Order") VALUES ('{PartId}', '{VolumeId}', 'P', '{Key()}');
                 INSERT INTO Chapters (Id, PartId, Title, "Order") VALUES ('{ChapterId}', '{PartId}', 'C', '{Key()}');
                 INSERT INTO Paragraphs (Id, ChapterId, "Order") VALUES ('{ParagraphId}', '{ChapterId}', '{Key()}');

                 INSERT INTO ParagraphItems (Id, ParagraphId, "Order", ItemType, Text, CharacterId) VALUES
                   ('{NarrationWithSpeaker}',    '{ParagraphId}', 'a0', 'Narration',    'He walked on.',      '{narrator}'),
                   ('{NarrationWithoutSpeaker}', '{ParagraphId}', 'a1', 'Narration',    'The room was cold.', {Speaker(unstamped)}),
                   ('{InsertedTitle}',           '{ParagraphId}', 'a2', 'Narration',    'Chapter One',        {Speaker(unstamped)}),
                   ('{DialogAttributed}',        '{ParagraphId}', 'a3', 'Character',    '"Hello," ',          '{AliceId}'),
                   ('{DialogUnattributed}',      '{ParagraphId}', 'a4', 'Character',    '"Goodbye."',         NULL),
                   ('{PauseItem}',               '{ParagraphId}', 'a5', 'ChapterPause', NULL,                 NULL);
                 """);
#pragma warning restore EF1002
        }

        private async Task<Dictionary<Guid, (ParagraphItemType Type, Guid? Speaker)>> ReadItemsAsync(string folder)
        {
            await using var db = OpenUnmigratedAt(folder);
            return await db.ParagraphItems
                .AsNoTracking()
                .ToDictionaryAsync(i => i.Id, i => (i.ItemType, i.CharacterId));
        }

        /// <summary>Runs the real migrator over the folder, exactly as opening the project does.</summary>
        private async Task MigrateUpAsync(string folder)
        {
            await using var db = OpenUnmigratedAt(folder);
            await db.Database.MigrateAsync();
        }

        [Fact]
        public async Task Migration_StampsTheNarrator_OnNarrationItemsThatHadNoSpeaker()
        {
            await SeedAtPriorMigrationAsync(FolderName);

            await MigrateUpAsync(FolderName);

            var items = await ReadItemsAsync(FolderName);
            Assert.Equal(ProjectDbContext.NarratorId, items[NarrationWithoutSpeaker].Speaker);
            Assert.Equal(ProjectDbContext.NarratorId, items[InsertedTitle].Speaker);
            Assert.Equal(ProjectDbContext.NarratorId, items[NarrationWithSpeaker].Speaker);
        }

        [Fact]
        public async Task Migration_LeavesDialogAndPausesAlone()
        {
            await SeedAtPriorMigrationAsync(FolderName);

            await MigrateUpAsync(FolderName);

            var items = await ReadItemsAsync(FolderName);
            Assert.Equal(AliceId, items[DialogAttributed].Speaker);
            Assert.Null(items[DialogUnattributed].Speaker);
            Assert.Null(items[PauseItem].Speaker);
            Assert.Equal(ParagraphItemType.ChapterPause, items[PauseItem].Type);
            Assert.Equal(ParagraphItemType.Character, items[DialogAttributed].Type);
            Assert.Equal(ParagraphItemType.Narration, items[NarrationWithoutSpeaker].Type);
        }

        [Fact]
        public async Task Migration_ChangesNothing_OnADatabaseThatAlreadySatisfiesTheInvariant()
        {
            await SeedAtPriorMigrationAsync(FolderName, conforming: true);
            var before = await ReadItemsAsync(FolderName);

            await MigrateUpAsync(FolderName);

            Assert.Equal(before, await ReadItemsAsync(FolderName));
        }

        /// <summary>
        /// What a producer sees after opening an old book is what they saw before: the same one
        /// unattributed item, the same Character paragraph, the same badges. The readers now derive
        /// all of that from the speaker (ADR-0006), which is exactly why the backfill has to have
        /// run first — a narration row still carrying a null speaker would read as unattributed
        /// dialog and land in the attribution queue.
        /// </summary>
        [Fact]
        public async Task Migration_LeavesAttributionCountsQueueAndBadgesUnchanged()
        {
            await SeedAtPriorMigrationAsync(FolderName);
            await MigrateUpAsync(FolderName);

            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            await using var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            var reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            var folder = new ProjectFolderId(FolderName);

            // Only DialogUnattributed has no speaker; the three narration items carry the narrator.
            Assert.Equal(1, await reader.CountUnattributedCharacterItemsAsync(folder, ParagraphId));

            // The paragraph has dialog in it, so it is a Character paragraph at every level.
            var overview = await reader.GetBookOverviewAsync(folder);
            Assert.Equal(
                new Dictionary<Guid, int> { [ChapterId] = 1, [PartId] = 1, [VolumeId] = 1 },
                overview.NodeCharacterParagraphCounts);
            Assert.Equal([ChapterId, VolumeId, PartId], overview.SelectableNodeIds.Order());
            Assert.Equal([ChapterId, VolumeId, PartId], (await reader.GetNodesWithCharacterParagraphsAsync(folder)).Order());

            // It still has attribution work outstanding, so it is in the queue.
            var unprocessed = await reader.GetCharacterParagraphsAsync(
                folder, BookNodeLevel.Chapter, ChapterId, unprocessedOnly: true);
            Assert.Equal(ParagraphId, Assert.Single(unprocessed).ParagraphId);

            // The badge seed counts the five speech items as missing audio, one of them unattributed.
            var seed = Assert.Single(await reader.GetNodeStatusSeedAsync(folder));
            Assert.Equal(new ParagraphStatusSeedRow(ParagraphId, ChapterId, PartId, VolumeId, 1, 5, 0), seed);
        }
    }
}
