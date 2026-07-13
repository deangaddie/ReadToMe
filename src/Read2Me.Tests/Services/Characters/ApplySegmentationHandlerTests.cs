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
    public class ApplySegmentationHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly EventBroadcaster<ParagraphItemsChanged> _events;
        private readonly ProjectFolderId _folder;

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();

        public ApplySegmentationHandlerTests()
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
                .WithCharacter("bob", new Character { Id = BobId, Name = "Bob" });

        private static SegmentSpec Narration(string text, string? instructions = null) =>
            new(text, SegmentItemType.Narration, null, instructions);

        private static SegmentSpec Dialog(string text, Guid? characterId, string? instructions = null) =>
            new(text, SegmentItemType.Character, characterId, instructions);

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

        [Fact]
        public async Task All_segments_match_keeps_item_ids_order_keys_and_audio()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello,\" ", "alice")
                .AddNarration("i2", "she said.")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");
            await SetAudioAsync(b.ItemId("i2"), "b.wav");

            var before = await ItemsAsync(b.ParagraphId("p"));

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Hello,\" ", AliceId, "warm"),
                Narration("she said."),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(before.Select(i => i.Id), after.Select(i => i.Id));
            Assert.Equal(before.Select(i => i.Order), after.Select(i => i.Order));
            Assert.Equal(["a.wav", "b.wav"], after.Select(i => i.AudioFileName));
            Assert.Equal("warm", after[0].VoiceInstructions);
            Assert.Equal(AliceId, after[0].CharacterId);
        }

        [Fact]
        public async Task Match_tolerates_quote_and_whitespace_normalization()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "“Hello,”  she said.", "alice")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Hello,\" she said.", AliceId),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            var item = Assert.Single(after);
            Assert.Equal(b.ItemId("i1"), item.Id);
            Assert.Equal("a.wav", item.AudioFileName);
            Assert.Equal("“Hello,”  she said.", item.Text);
        }

        [Fact]
        public async Task Instructions_only_change_keeps_audio()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Run!\"", "alice")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Run!\"", AliceId, "shouting"),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal("a.wav", item.AudioFileName);
            Assert.Equal("shouting", item.VoiceInstructions);
        }

        [Fact]
        public async Task Resplit_replaces_rows_and_orders_new_items_in_segment_order()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "\"Go,\" said Alice. \"No,\" said Bob.")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "old.wav");

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Go,\" ", AliceId),
                Narration("said Alice. "),
                Dialog("\"No,\" ", BobId),
                Narration("said Bob."),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(4, after.Count);
            Assert.DoesNotContain(after, i => i.Id == b.ItemId("i1"));
            Assert.Equal(
                ["\"Go,\" ", "said Alice. ", "\"No,\" ", "said Bob."],
                after.Select(i => i.Text));
            Assert.Equal(
                [ParagraphItemType.Character, ParagraphItemType.Narration, ParagraphItemType.Character, ParagraphItemType.Narration],
                after.Select(i => i.ItemType));
            Assert.Equal([AliceId, ProjectDbContext.NarratorId, BobId, ProjectDbContext.NarratorId],
                after.Select(i => i.CharacterId));
            Assert.All(after, i => Assert.Null(i.AudioFileName));
        }

        [Fact]
        public async Task Unknown_speaker_on_matched_item_preserves_existing_stamp()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Hello.\"", characterId: null),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal(AliceId, item.CharacterId);
        }

        [Fact]
        public async Task Unknown_speaker_on_new_item_stays_null()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Who goes there?\"", characterId: null),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.NotEqual(b.ItemId("i1"), item.Id);
            Assert.Null(item.CharacterId);
        }

        [Fact]
        public async Task New_segment_inserts_between_kept_neighbours_without_renumbering_them()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Go,\" ", "alice")
                .AddNarration("i2", "said Bob.")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");
            await SetAudioAsync(b.ItemId("i2"), "b.wav");
            var before = await ItemsAsync(b.ParagraphId("p"));

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Go,\" ", AliceId),
                Narration("he begged. "),
                Narration("said Bob."),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(["\"Go,\" ", "he begged. ", "said Bob."], after.Select(i => i.Text));
            Assert.Equal(b.ItemId("i1"), after[0].Id);
            Assert.Equal(b.ItemId("i2"), after[2].Id);
            Assert.Equal(before[0].Order, after[0].Order);
            Assert.Equal(before[1].Order, after[2].Order);
            Assert.Equal(["a.wav", null, "b.wav"], after.Select(i => i.AudioFileName));
        }

        [Fact]
        public async Task Pause_items_are_untouched_and_stay_after_replaced_text()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")
                .AddPause("pause")))).BuildAsync();
            var before = await ItemsAsync(b.ParagraphId("p"));

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Narration("He "),
                Narration("waited."),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(3, after.Count);
            Assert.Equal(["He ", "waited.", null], after.Select(i => i.Text));
            var pause = after[2];
            Assert.Equal(b.ItemId("pause"), pause.Id);
            Assert.Equal(ParagraphItemType.Pause, pause.ItemType);
            Assert.Equal(before.Single(i => i.Id == b.ItemId("pause")).Order, pause.Order);
        }

        [Fact]
        public async Task Interleaved_pause_survives_a_full_resplit_and_lands_after_the_new_text()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")
                .AddPause("pause")
                .AddNarration("i2", "She left.")))).BuildAsync();
            var pauseOrder = (await ItemsAsync(b.ParagraphId("p"))).Single(i => i.Id == b.ItemId("pause")).Order;

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Narration("He waited and "),
                Narration("she left."),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(["He waited and ", "she left.", null], after.Select(i => i.Text));
            Assert.Equal(b.ItemId("pause"), after[2].Id);
            Assert.Equal(pauseOrder, after[2].Order);
        }

        [Fact]
        public async Task Null_instructions_clear_existing_instructions_on_a_matched_item()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Run!\"", "alice")))).BuildAsync();
            await using (var db = await OpenDbAsync())
            {
                var seeded = await db.ParagraphItems.FirstAsync(i => i.Id == b.ItemId("i1"));
                seeded.VoiceInstructions = "stale";
                await db.SaveChangesAsync();
            }

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Dialog("\"Run!\"", AliceId, instructions: null),
            ]));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Null(item.VoiceInstructions);
        }

        [Fact]
        public async Task Publishes_ParagraphItemsChanged_once()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")))).BuildAsync();

            var received = new List<ParagraphItemsChanged>();
            _events.Event += e => received.Add(e);

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"),
            [
                Narration("He waited."),
            ]));

            var e = Assert.Single(received);
            Assert.Equal(_folder, e.FolderId);
            Assert.Equal(b.ParagraphId("p"), e.ParagraphId);
        }

        [Fact]
        public async Task Unknown_paragraph_is_a_no_op_and_publishes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")))).BuildAsync();

            var received = new List<ParagraphItemsChanged>();
            _events.Event += e => received.Add(e);

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, Guid.NewGuid(),
            [
                Narration("Nothing."),
            ]));

            Assert.Empty(received);
            Assert.Single(await ItemsAsync(b.ParagraphId("p")));
        }

        [Fact]
        public async Task Empty_segment_list_is_a_no_op()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")))).BuildAsync();

            await _svc.ExecuteAsync(new ApplySegmentationCommand(_folder, b.ParagraphId("p"), []));

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal(b.ItemId("i1"), item.Id);
        }
    }
}
