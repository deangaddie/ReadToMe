using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The bulk-assign confirm dialog's figures come from a read, not from the write: the
    /// selection can cover chapters that were never expanded, so the items are not in memory.
    /// </summary>
    public class BulkAssignPreviewReaderTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public BulkAssignPreviewReaderTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private static readonly Guid AliceId = Guid.NewGuid();

        /// <summary>
        /// p1: two dialog items plus narration and a pause. p2: one dialog item.
        /// p3: narration only. p4: a lone pause. p5: dialog, but outside every selection below.
        /// </summary>
        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" });
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph("p1", p => p
                        .AddRawItem("p1-dialog-a", ParagraphItemType.Character, "\"One.\"", AliceId)
                        .AddRawItem("p1-dialog-b", ParagraphItemType.Character, "\"Still one.\"", null)
                        .AddRawItem("p1-narration", ParagraphItemType.Narration, "he said.", ProjectDbContext.NarratorId)
                        .AddRawItem("p1-pause", ParagraphItemType.Pause, null))
                    .AddParagraph("p2", p => p
                        .AddRawItem("p2-dialog", ParagraphItemType.Character, "\"Two.\"", null)
                        .AddRawItem("p2-narration", ParagraphItemType.Narration, "she said.", ProjectDbContext.NarratorId))
                    .AddParagraph("p3", p => p
                        .AddRawItem("p3-narration", ParagraphItemType.Narration, "Nobody spoke.", ProjectDbContext.NarratorId))
                    .AddParagraph("p4", p => p
                        .AddRawItem("p4-pause", ParagraphItemType.Pause, null))
                    .AddParagraph("p5", p => p
                        .AddRawItem("p5-dialog", ParagraphItemType.Character, "\"Five.\"", AliceId))))
                .BuildAsync();
            return b;
        }

        [Fact]
        public async Task GetBulkAssignPreview_CountsCharacterItemsAndTheParagraphsHoldingThem()
        {
            var b = await SeedAsync();

            var preview = await _reader.GetBulkAssignPreviewAsync(
                _folder, [b.ParagraphId("p1"), b.ParagraphId("p2"), b.ParagraphId("p3"), b.ParagraphId("p4")]);

            // p1 contributes two items, p2 one; p3 (narration) and p4 (pause) contribute nothing.
            Assert.Equal(3, preview.CharacterItems);
            Assert.Equal(2, preview.ParagraphsWithCharacterItems);
        }

        [Fact]
        public async Task GetBulkAssignPreview_ParagraphsWithoutDialog_ContributeNothing()
        {
            var b = await SeedAsync();

            var preview = await _reader.GetBulkAssignPreviewAsync(
                _folder, [b.ParagraphId("p3"), b.ParagraphId("p4")]);

            Assert.Equal(0, preview.CharacterItems);
            Assert.Equal(0, preview.ParagraphsWithCharacterItems);
        }

        [Fact]
        public async Task GetBulkAssignPreview_IsCharacterAgnostic_AlreadyStampedItemsStillCount()
        {
            var b = await SeedAsync();

            // p1's two dialog items differ: one already points at Alice, one at nobody. The
            // confirm states what will be written, not what changes, so both count.
            var preview = await _reader.GetBulkAssignPreviewAsync(_folder, [b.ParagraphId("p1")]);

            Assert.Equal(2, preview.CharacterItems);
            Assert.Equal(1, preview.ParagraphsWithCharacterItems);
        }

        [Fact]
        public async Task GetBulkAssignPreview_EmptyIdList_IsZeroes()
        {
            await SeedAsync();

            var preview = await _reader.GetBulkAssignPreviewAsync(_folder, []);

            Assert.Equal(0, preview.CharacterItems);
            Assert.Equal(0, preview.ParagraphsWithCharacterItems);
        }

        [Fact]
        public async Task GetBulkAssignPreview_IdsThisFolderDoesNotHold_AreIgnored()
        {
            var b = await SeedAsync();

            var preview = await _reader.GetBulkAssignPreviewAsync(
                _folder, [b.ParagraphId("p2"), Guid.NewGuid(), Guid.NewGuid()]);

            // Only p2; the foreign ids match nothing, and p5 is never in the list.
            Assert.Equal(1, preview.CharacterItems);
            Assert.Equal(1, preview.ParagraphsWithCharacterItems);
        }
    }
}
