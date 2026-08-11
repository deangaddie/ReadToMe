using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class AttributeItemsHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly EventBroadcaster<ParagraphItemsChanged> _events;
        private readonly ProjectFolderId _folder;

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();
        private static readonly Guid CarolId = Guid.NewGuid();

        public AttributeItemsHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _svc = sp.GetRequiredService<BookCommandHandler>();
            _events = sp.GetRequiredService<EventBroadcaster<ParagraphItemsChanged>>();
            _folder = new ProjectFolderId(FolderName);
        }

        private BookHierarchyBuilder Builder() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .WithCharacter("bob", new Character { Id = BobId, Name = "Bob" })
                .WithCharacter("carol", new Character { Id = CarolId, Name = "Carol" });

        private async Task<List<ParagraphItem>> ItemsAsync(Guid paragraphId)
        {
            await using var db = await OpenDbAsync();
            var items = await db.ParagraphItems.Where(i => i.ParagraphId == paragraphId).ToListAsync();
            items.Sort((a, b) => string.CompareOrdinal(a.Order, b.Order));
            return items;
        }

        private async Task SetAudioAsync(Guid itemId, string fileName)
        {
            await using var db = await OpenDbAsync();
            var item = await db.ParagraphItems.FirstAsync(i => i.Id == itemId);
            item.AudioFileName = fileName;
            await db.SaveChangesAsync();
        }

        private async Task SetInstructionsAsync(Guid itemId, string instructions)
        {
            await using var db = await OpenDbAsync();
            var item = await db.ParagraphItems.FirstAsync(i => i.Id == itemId);
            item.VoiceInstructions = instructions;
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Stamps_speaker_and_instructions_keeping_text_order_and_audio()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello,\" ", "alice")
                .AddNarration("i2", "she said.")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");
            var before = await ItemsAsync(b.ParagraphId("p"));

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), BobId, "warm"),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(before.Select(i => i.Id), after.Select(i => i.Id));
            Assert.Equal(before.Select(i => i.Order), after.Select(i => i.Order));
            Assert.Equal(before.Select(i => i.Text), after.Select(i => i.Text));
            Assert.Equal(before.Select(i => i.ItemType), after.Select(i => i.ItemType));
            Assert.Equal("a.wav", after[0].AudioFileName);
            Assert.Equal(BobId, after[0].CharacterId);
            Assert.Equal("warm", after[0].VoiceInstructions);
        }

        [Fact]
        public async Task Null_instructions_clear_existing_instructions()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Run!\"", "alice")))).BuildAsync();
            await SetInstructionsAsync(b.ItemId("i1"), "stale");

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), AliceId, null),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Null(item.VoiceInstructions);
        }

        [Fact]
        public async Task Null_character_leaves_an_existing_stamp_but_still_writes_instructions()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), null, "unsure"),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal(AliceId, item.CharacterId);
            Assert.Equal("unsure", item.VoiceInstructions);
        }

        [Fact]
        public async Task Unanswered_item_is_left_alone()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Go,\" ", "alice")
                .AddCharacterLine("i2", "\"No,\"", "bob")))).BuildAsync();
            await SetInstructionsAsync(b.ItemId("i2"), "flat");

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), CarolId, null),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(2, after.Count);
            Assert.Equal(CarolId, after[0].CharacterId);
            Assert.Equal(BobId, after[1].CharacterId);
            Assert.Equal("flat", after[1].VoiceInstructions);
        }

        [Fact]
        public async Task Item_id_from_another_paragraph_is_ignored()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c
                .AddParagraph("p", p => p.AddCharacterLine("i1", "\"Hello.\"", "alice"))
                .AddParagraph("q", p => p.AddCharacterLine("i2", "\"Bye.\"", "bob")))).BuildAsync();

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i2"), AliceId, "shouted"),
            ]));

            var foreign = Assert.Single(await ItemsAsync(b.ParagraphId("q")));
            Assert.Equal(BobId, foreign.CharacterId);
            Assert.Null(foreign.VoiceInstructions);
            Assert.Single(await ItemsAsync(b.ParagraphId("p")));
        }

        [Fact]
        public async Task Stale_item_id_is_ignored_and_the_rest_still_apply()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(Guid.NewGuid(), BobId, "gone"),
                new ItemAttribution(b.ItemId("i1"), BobId, "kept"),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal(BobId, item.CharacterId);
            Assert.Equal("kept", item.VoiceInstructions);
        }

        [Fact]
        public async Task Pause_items_are_untouched()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")
                .AddPause("pause")))).BuildAsync();
            var before = await ItemsAsync(b.ParagraphId("p"));

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("pause"), AliceId, "breathy"),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(before.Count, after.Count);
            var pause = after.Single(i => i.Id == b.ItemId("pause"));
            Assert.Equal(ParagraphItemType.Pause, pause.ItemType);
            Assert.Null(pause.CharacterId);
            Assert.Null(pause.VoiceInstructions);
        }

        [Fact]
        public async Task Narration_items_are_untouched()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello,\" ", "alice")
                .AddNarration("i2", "she said.")))).BuildAsync();
            var narratorBefore = (await ItemsAsync(b.ParagraphId("p")))[1].CharacterId;

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i2"), CarolId, "breathless"),
            ]));

            var narration = (await ItemsAsync(b.ParagraphId("p")))[1];
            Assert.Equal(narratorBefore, narration.CharacterId);
            Assert.Null(narration.VoiceInstructions);
        }

        [Fact]
        public async Task Item_count_is_invariant_even_when_nothing_matches()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Go,\" ", "alice")
                .AddNarration("i2", "said Alice.")
                .AddPause("pause")))).BuildAsync();

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(Guid.NewGuid(), BobId, "x"),
                new ItemAttribution(Guid.NewGuid(), null, null),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(3, after.Count);
            Assert.Equal(["\"Go,\" ", "said Alice.", null], after.Select(i => i.Text));
        }

        [Fact]
        public async Task Publishes_ParagraphItemsChanged_once()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")
                .AddCharacterLine("i2", "\"Bye.\"", "bob")))).BuildAsync();

            var received = new List<ParagraphItemsChanged>();
            _events.Event += e => received.Add(e);

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), CarolId, null),
                new ItemAttribution(b.ItemId("i2"), CarolId, null),
            ]));

            var e = Assert.Single(received);
            Assert.Equal(_folder, e.FolderId);
            Assert.Equal(b.ParagraphId("p"), e.ParagraphId);
        }

        [Fact]
        public async Task An_answer_that_stamps_nothing_publishes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            var received = new List<ParagraphItemsChanged>();
            _events.Event += e => received.Add(e);

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(Guid.NewGuid(), CarolId, "stale"),
            ]));

            Assert.Empty(received);
        }

        [Fact]
        public async Task Unknown_paragraph_is_a_no_op_and_publishes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            var received = new List<ParagraphItemsChanged>();
            _events.Event += e => received.Add(e);

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, Guid.NewGuid(),
            [
                new ItemAttribution(b.ItemId("i1"), BobId, "nope"),
            ]));

            Assert.Empty(received);
            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal(AliceId, item.CharacterId);
        }

        [Fact]
        public async Task Empty_attribution_list_is_a_no_op()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")))).BuildAsync();

            var received = new List<ParagraphItemsChanged>();
            _events.Event += e => received.Add(e);

            await _svc.ExecuteAsync(new AttributeItemsCommand(_folder, b.ParagraphId("p"), []));

            Assert.Empty(received);
            Assert.Single(await ItemsAsync(b.ParagraphId("p")));
        }
    }
}
