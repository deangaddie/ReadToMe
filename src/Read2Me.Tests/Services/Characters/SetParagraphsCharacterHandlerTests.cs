using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The bulk assign's sweep rules, still asserted through the command the generic endpoint
    /// posts — its request and response shape are unchanged by the move to
    /// <c>BookMutations</c> (ADR 0007). What the write reports to a Book View is asserted on the
    /// mutation itself, in <c>SpeakerAttributionMutationTests</c>.
    /// </summary>
    public class SetParagraphsCharacterHandlerTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly SetParagraphsCharacterHandler _handler;
        private readonly ProjectFolderId _folder;

        public SetParagraphsCharacterHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();

            _handler = new SetParagraphsCharacterHandler(_root.GetRequiredService<BookMutations>());
            _folder = new ProjectFolderId(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        /// <summary>
        /// Two selected paragraphs, each a mix of Character / Narration / Pause, plus a third
        /// paragraph that stands outside the selection.
        /// </summary>
        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" });
            b.WithCharacter("bob", new Character { Id = BobId, Name = "Bob" });
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                    .AddParagraph("p1", p => p
                        .AddRawItem("p1-dialog", ParagraphItemType.Speech, "\"One.\"", AliceId)
                        .AddRawItem("p1-narration", ParagraphItemType.Speech, "he said.", ProjectDbContext.NarratorId)
                        .AddRawItem("p1-pause", ParagraphItemType.Pause, null))
                    .AddParagraph("p2", p => p
                        .AddRawItem("p2-dialog", ParagraphItemType.Speech, "\"Two.\"", null)
                        .AddRawItem("p2-narration", ParagraphItemType.Speech, "she said.", ProjectDbContext.NarratorId))
                    .AddParagraph("p3", p => p
                        .AddRawItem("p3-dialog", ParagraphItemType.Speech, "\"Three.\"", AliceId))))
                .BuildAsync();
            return b;
        }

        private async Task<Guid?> CharacterIdOfAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            var item = await db.ParagraphItems.FindAsync(itemId);
            return item!.CharacterId;
        }

        [Fact]
        public async Task SetParagraphsCharacter_StampsOnlyCharacterItems_AcrossEveryListedParagraph()
        {
            var b = await SeedAsync();

            await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [b.ParagraphId("p1"), b.ParagraphId("p2")], BobId),
                CancellationToken.None);

            Assert.Equal(BobId, await CharacterIdOfAsync(b.ItemId("p1-dialog")));
            Assert.Equal(BobId, await CharacterIdOfAsync(b.ItemId("p2-dialog")));
            // Narration keeps the narrator, a pause keeps nobody.
            Assert.Equal(ProjectDbContext.NarratorId, await CharacterIdOfAsync(b.ItemId("p1-narration")));
            Assert.Equal(ProjectDbContext.NarratorId, await CharacterIdOfAsync(b.ItemId("p2-narration")));
            Assert.Null(await CharacterIdOfAsync(b.ItemId("p1-pause")));
        }

        [Fact]
        public async Task SetParagraphsCharacter_WithNullId_ClearsTheSpeaker()
        {
            var b = await SeedAsync();

            await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [b.ParagraphId("p1")], null),
                CancellationToken.None);

            Assert.Null(await CharacterIdOfAsync(b.ItemId("p1-dialog")));
            Assert.Equal(ProjectDbContext.NarratorId, await CharacterIdOfAsync(b.ItemId("p1-narration")));
        }

        [Fact]
        public async Task SetParagraphsCharacter_ParagraphsOutsideTheList_AreUntouched()
        {
            var b = await SeedAsync();

            await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [b.ParagraphId("p1")], BobId),
                CancellationToken.None);

            Assert.Equal(AliceId, await CharacterIdOfAsync(b.ItemId("p3-dialog")));
            Assert.Null(await CharacterIdOfAsync(b.ItemId("p2-dialog")));
        }

        [Fact]
        public async Task SetParagraphsCharacter_EmptyIdList_IsANoOp()
        {
            var b = await SeedAsync();

            var ex = await Record.ExceptionAsync(() => _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [], BobId),
                CancellationToken.None));

            Assert.Null(ex);
            Assert.Equal(AliceId, await CharacterIdOfAsync(b.ItemId("p1-dialog")));
            Assert.Null(await CharacterIdOfAsync(b.ItemId("p2-dialog")));
            Assert.Equal(AliceId, await CharacterIdOfAsync(b.ItemId("p3-dialog")));
        }

        [Fact]
        public async Task SetParagraphsCharacter_LargeSelection_NarrationSurvivesEverywhere()
        {
            // The failure this rule exists to prevent: one gesture turning a chapter's narration
            // into dialog. Fifty paragraphs, each narration + dialog, all selected at once.
            const int paragraphCount = 50;
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" });
            b.WithCharacter("bob", new Character { Id = BobId, Name = "Bob" });
            await b.AddVolume("vol", v => v.AddChapter(configure: c =>
            {
                for (var i = 0; i < paragraphCount; i++)
                {
                    var n = i;
                    c.AddParagraph($"p{n}", p => p
                        .AddNarration($"n{n}", "he said.")
                        .AddRawItem($"d{n}", ParagraphItemType.Speech, "\"Line.\"", AliceId));
                }
            })).BuildAsync();

            var paragraphIds = Enumerable.Range(0, paragraphCount).Select(i => b.ParagraphId($"p{i}")).ToList();
            await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, paragraphIds, BobId),
                CancellationToken.None);

            for (var i = 0; i < paragraphCount; i++)
            {
                Assert.Equal(BobId, await CharacterIdOfAsync(b.ItemId($"d{i}")));
                Assert.Equal(ProjectDbContext.NarratorId, await CharacterIdOfAsync(b.ItemId($"n{i}")));
            }
        }

        [Fact]
        public async Task SetParagraphsCharacter_ToNarrator_MakesTheSelectionNarration()
        {
            var b = await SeedAsync();

            await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [b.ParagraphId("p1"), b.ParagraphId("p2")], ProjectDbContext.NarratorId),
                CancellationToken.None);

            Assert.Equal(ProjectDbContext.NarratorId, await CharacterIdOfAsync(b.ItemId("p1-dialog")));
            Assert.Equal(ProjectDbContext.NarratorId, await CharacterIdOfAsync(b.ItemId("p2-dialog")));
            Assert.Null(await CharacterIdOfAsync(b.ItemId("p1-pause")));
            // Outside the selection, unchanged.
            Assert.Equal(AliceId, await CharacterIdOfAsync(b.ItemId("p3-dialog")));
        }

        [Fact]
        public async Task SetParagraphsCharacter_DropsAudioOnlyFromItemsItMoves()
        {
            var b = await SeedAsync();
            await using (var seed = await OpenDbAsync())
            {
                foreach (var name in new[] { "p1-dialog", "p1-narration", "p3-dialog" })
                    (await seed.ParagraphItems.FindAsync(b.ItemId(name)))!.AudioFileName = $"audio/{name}.wav";
                await seed.SaveChangesAsync();
            }

            await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [b.ParagraphId("p1")], BobId),
                CancellationToken.None);

            await using var verify = await OpenDbAsync();
            Assert.Null((await verify.ParagraphItems.FindAsync(b.ItemId("p1-dialog")))!.AudioFileName);
            Assert.NotNull((await verify.ParagraphItems.FindAsync(b.ItemId("p1-narration")))!.AudioFileName);
            Assert.NotNull((await verify.ParagraphItems.FindAsync(b.ItemId("p3-dialog")))!.AudioFileName);
        }

        /// <summary>A bulk assign creates nothing, so the bus gets no new id to hand back.</summary>
        [Fact]
        public async Task SetParagraphsCharacter_ReturnsNull_NoNewIdIsCreated()
        {
            var b = await SeedAsync();

            var result = await _handler.HandleAsync(
                new SetParagraphsCharacterCommand(_folder, [b.ParagraphId("p1")], BobId),
                CancellationToken.None);

            Assert.Null(result);
        }
    }
}
