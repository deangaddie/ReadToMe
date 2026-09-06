using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Mutations
{
    /// <summary>
    /// The speaker attribution family — the Character Queue's answer and the three by-hand
    /// gestures — proved through <see cref="BookMutations.CommitAsync"/> against a real SQLite
    /// project.
    /// <para>
    /// Two things matter here beyond the stamping rules themselves. The receipt must name exactly
    /// the Paragraphs and ParagraphItems that moved, because this is the family a Book View
    /// refreshes from instead of rebuilding; and an answer that agrees with what is already stamped
    /// must be <see cref="BookMutationOutcome.NoChange"/>, because a queue working through a
    /// re-attributed chapter would otherwise make every open Book View reread it for nothing.
    /// </para>
    /// </summary>
    public class SpeakerAttributionMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        private static readonly Guid AliceId = Guid.NewGuid();
        private static readonly Guid BobId = Guid.NewGuid();
        private static readonly Guid CarolId = Guid.NewGuid();

        public SpeakerAttributionMutationTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _folder = new ProjectFolderId(FolderName);
        }

        /// <summary>Commits in its own scope, the way a producer does, and returns the outcome.</summary>
        private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
        }

        private async Task<BookMutationReceipt> CommittedAsync(BookMutation mutation) =>
            Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(mutation)).Receipt;

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
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

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i2"), CarolId, "breathless"),
            ]));

            var narration = (await ItemsAsync(b.ParagraphId("p")))[1];
            Assert.Equal(narratorBefore, narration.CharacterId);
            Assert.Null(narration.VoiceInstructions);
        }

        [Fact]
        public async Task An_item_the_user_narrated_survives_a_rerun_untouched()
        {
            // Assigning an item to the narrator is also the gesture that locks it out of
            // re-attribution (ADR-0006) — the queue may not undo a decision made by hand.
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddRawItem("narrated", ParagraphItemType.Speech, "\"Hello,\" ", ProjectDbContext.NarratorId)
                .AddRawItem("dialog", ParagraphItemType.Speech, "\"Hi,\"", null)))).BuildAsync();

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("narrated"), CarolId, "breathless"),
                new ItemAttribution(b.ItemId("dialog"), CarolId, null),
            ]));

            var items = await ItemsAsync(b.ParagraphId("p"));
            var narrated = items.Single(i => i.Id == b.ItemId("narrated"));
            Assert.Equal(ProjectDbContext.NarratorId, narrated.CharacterId);
            Assert.Null(narrated.VoiceInstructions);
            // Its non-narrator neighbour is still asked and stamped.
            Assert.Equal(CarolId, items.Single(i => i.Id == b.ItemId("dialog")).CharacterId);
        }

        [Fact]
        public async Task A_rerun_restamps_an_already_attributed_item()
        {
            // Pre-change behaviour: requesting a re-run re-asks every non-narrator item, whether
            // or not it already carries a speaker.
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello,\"", "alice")))).BuildAsync();

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), CarolId, null),
            ]));

            Assert.Equal(CarolId, (await ItemsAsync(b.ParagraphId("p")))[0].CharacterId);
        }

        [Fact]
        public async Task Item_count_is_invariant_even_when_nothing_matches()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Go,\" ", "alice")
                .AddNarration("i2", "said Alice.")
                .AddPause("pause")))).BuildAsync();

            await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(Guid.NewGuid(), BobId, "x"),
                new ItemAttribution(Guid.NewGuid(), null, null),
            ]));

            var after = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(3, after.Count);
            Assert.Equal(["\"Go,\" ", "said Alice.", null], after.Select(i => i.Text));
        }

        // ── what the queue's answer reports ──────────────────────────────────

        [Fact]
        public async Task An_answer_reports_the_paragraph_and_exactly_the_items_it_stamped()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")
                .AddCharacterLine("i2", "\"Bye.\"", "bob")
                .AddNarration("i3", "she said.")))).BuildAsync();

            var receipt = await CommittedAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
            [
                new ItemAttribution(b.ItemId("i1"), CarolId, null),
                // Narration is never stamped, so it is never reported either.
                new ItemAttribution(b.ItemId("i3"), CarolId, null),
            ]));

            Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
            // The queue leaves generated audio alone, so a Book View has no audio to invalidate.
            Assert.Equal(BookFacets.Attribution, receipt.Effects.Facets);
            Assert.Equal([b.ParagraphId("p")], receipt.Effects.ParagraphIds);
            Assert.Equal([b.ItemId("i1")], receipt.Effects.ParagraphItemIds);
        }

        [Fact]
        public async Task An_answer_that_stamps_nothing_changes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
                [
                    new ItemAttribution(Guid.NewGuid(), CarolId, "stale"),
                ])));
        }

        [Fact]
        public async Task An_answer_that_agrees_with_what_is_already_stamped_changes_nothing()
        {
            // The re-attribution case: a queue run over an already-attributed chapter must not
            // consume a revision — and make every open Book View reread — per unchanged paragraph.
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"),
                [
                    new ItemAttribution(b.ItemId("i1"), AliceId, null),
                ])));
        }

        [Fact]
        public async Task An_answer_for_a_paragraph_the_book_no_longer_has_is_not_found()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            // Expected, not a defect: the queue is asynchronous, and the paragraph it asked about
            // can have been deleted while the answer was in flight.
            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(
                await CommitAsync(new AttributeParagraphItemsMutation(_folder, Guid.NewGuid(),
                [
                    new ItemAttribution(b.ItemId("i1"), BobId, "nope"),
                ])));
            Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);

            var item = Assert.Single(await ItemsAsync(b.ParagraphId("p")));
            Assert.Equal(AliceId, item.CharacterId);
        }

        [Fact]
        public async Task Empty_attribution_list_changes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddNarration("i1", "He waited.")))).BuildAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new AttributeParagraphItemsMutation(_folder, b.ParagraphId("p"), [])));
            Assert.Single(await ItemsAsync(b.ParagraphId("p")));
        }

        // ── the by-hand gestures ─────────────────────────────────────────────

        [Fact]
        public async Task Stamping_one_item_by_hand_reports_it_and_its_paragraph_and_drops_its_audio()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")
                .AddCharacterLine("i2", "\"Bye.\"", "bob")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");

            var receipt = await CommittedAsync(new SetItemSpeakerMutation(_folder, b.ItemId("i1"), CarolId));

            Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
            // A hand-flip says the generated audio is in the wrong voice, so a reader is told to
            // reread the item's audio as well as its speaker.
            Assert.Equal(BookFacets.Attribution | BookFacets.Audio, receipt.Effects.Facets);
            Assert.Equal([b.ParagraphId("p")], receipt.Effects.ParagraphIds);
            Assert.Equal([b.ItemId("i1")], receipt.Effects.ParagraphItemIds);

            var items = await ItemsAsync(b.ParagraphId("p"));
            Assert.Equal(CarolId, items[0].CharacterId);
            Assert.Null(items[0].AudioFileName);
            Assert.Equal(BobId, items[1].CharacterId);
        }

        [Fact]
        public async Task Stamping_an_item_that_had_no_audio_does_not_claim_the_audio_facet()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            var receipt = await CommittedAsync(new SetItemSpeakerMutation(_folder, b.ItemId("i1"), CarolId));

            // Facets report what the write actually did. There was no generated audio to invalidate,
            // so a reader is not sent to reread any.
            Assert.Equal(BookFacets.Attribution, receipt.Effects.Facets);
        }

        [Fact]
        public async Task Stamping_an_item_with_the_speaker_it_already_has_changes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetItemSpeakerMutation(_folder, b.ItemId("i1"), AliceId)));
        }

        [Fact]
        public async Task Stamping_an_item_the_book_does_not_have_is_not_found()
        {
            await Builder().AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(
                await CommitAsync(new SetItemSpeakerMutation(_folder, Guid.NewGuid(), AliceId)));
            Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
        }

        [Fact]
        public async Task Stamping_a_paragraph_reports_only_the_dialog_items_it_swept()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello,\" ", "alice")
                .AddNarration("i2", "she said.")
                .AddPause("pause")))).BuildAsync();
            await SetAudioAsync(b.ItemId("i1"), "a.wav");

            var receipt = await CommittedAsync(
                new SetParagraphSpeakerMutation(_folder, b.ParagraphId("p"), CarolId));

            Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
            Assert.Equal(BookFacets.Attribution | BookFacets.Audio, receipt.Effects.Facets);
            Assert.Equal([b.ParagraphId("p")], receipt.Effects.ParagraphIds);
            Assert.Equal([b.ItemId("i1")], receipt.Effects.ParagraphItemIds);
        }

        [Fact]
        public async Task Stamping_a_paragraph_that_is_already_on_that_speaker_changes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetParagraphSpeakerMutation(_folder, b.ParagraphId("p"), AliceId)));
        }

        [Fact]
        public async Task Stamping_a_paragraph_the_book_does_not_have_is_not_found()
        {
            await Builder().AddVolume("v", v => v.AddChapter(configure: c => c.AddParagraph("p", p => p
                .AddCharacterLine("i1", "\"Hello.\"", "alice")))).BuildAsync();

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(
                await CommitAsync(new SetParagraphSpeakerMutation(_folder, Guid.NewGuid(), AliceId)));
            Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
        }

        [Fact]
        public async Task A_bulk_assign_reports_every_paragraph_and_item_it_moved_and_no_others()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c
                .AddParagraph("p1", p => p
                    .AddCharacterLine("i1", "\"Hello,\" ", "alice")
                    .AddNarration("n1", "she said."))
                .AddParagraph("p2", p => p.AddCharacterLine("i2", "\"Bye.\"", "bob"))
                .AddParagraph("p3", p => p.AddCharacterLine("i3", "\"Later.\"", "carol"))))
                .BuildAsync();
            await SetAudioAsync(b.ItemId("i2"), "b.wav");

            var receipt = await CommittedAsync(new SetParagraphsSpeakerMutation(
                _folder, [b.ParagraphId("p1"), b.ParagraphId("p2"), b.ParagraphId("p3")], CarolId));

            Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
            Assert.Equal(BookFacets.Attribution | BookFacets.Audio, receipt.Effects.Facets);
            // p3 is already Carol's and its narration was never in scope, so neither is reported.
            // A set, not a sequence: a receipt names what moved, and nothing reads an order into it.
            Assert.Equal(
                new HashSet<Guid> { b.ParagraphId("p1"), b.ParagraphId("p2") },
                receipt.Effects.ParagraphIds.ToHashSet());
            Assert.Equal(
                new HashSet<Guid> { b.ItemId("i1"), b.ItemId("i2") },
                receipt.Effects.ParagraphItemIds.ToHashSet());

            var narration = (await ItemsAsync(b.ParagraphId("p1")))[1];
            Assert.Equal(ProjectDbContext.NarratorId, narration.CharacterId);
        }

        [Fact]
        public async Task A_bulk_assign_over_a_selection_that_is_already_on_that_speaker_changes_nothing()
        {
            var b = Builder();
            await b.AddVolume("v", v => v.AddChapter(configure: c => c
                .AddParagraph("p1", p => p.AddCharacterLine("i1", "\"Hello.\"", "carol"))
                .AddParagraph("p2", p => p.AddNarration("n1", "She waited."))))
                .BuildAsync();

            Assert.IsType<BookMutationOutcome.NoChange>(
                await CommitAsync(new SetParagraphsSpeakerMutation(
                    _folder, [b.ParagraphId("p1"), b.ParagraphId("p2")], CarolId)));
        }
    }
}
