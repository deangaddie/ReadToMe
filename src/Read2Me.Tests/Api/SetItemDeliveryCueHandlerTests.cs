using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Api;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Tests.Infrastructure;
using Xunit;
using EntityReviewState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Tests.Api
{
    /// <summary>
    /// <c>SetItemDeliveryCue</c> as <c>POST /api/projects/{folder}/commands</c> answers it, over a real
    /// project: a producer's hand-written delivery cue on one speech item. It is normalised (trimmed,
    /// blank clears it), bounded, refused on a pause or an unknown item with a 422 rather than the
    /// quiet null its siblings answer, and a real change discards the item's audio and its review as
    /// a text edit does. The receipt on the live hub is DeliveryCueApiTests.
    /// </summary>
    public class SetItemDeliveryCueHandlerTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly ServiceProvider _root;

        public SetItemDeliveryCueHandlerTests()
        {
            _folder = new ProjectFolderId(FolderName);
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>One API request: its own scope, the endpoint's adapter, the wire's answer.</summary>
        private async Task<IResult> SetCueAsync(Guid itemId, string? cue)
        {
            await using var request = _root.CreateAsyncScope();
            var api = new BookCommandApiAdapter(request.ServiceProvider.GetRequiredService<BookCommandDispatcher>());
            return await api.ExecuteAsync(new SetItemDeliveryCueCommand(_folder, itemId, cue), CancellationToken.None);
        }

        private static void AssertOk(IResult result) => Assert.IsType<Ok<CommandResponse>>(result);

        private static void Assert422(IResult result) =>
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(result).StatusCode);

        private async Task<ParagraphItem> ItemAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return await db.ParagraphItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        }

        private async Task<string?> CueAsync(Guid itemId) => (await ItemAsync(itemId)).VoiceInstructions;

        private async Task<bool> HasReviewAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return await db.AudioReviews.AnyAsync(r => r.ParagraphItemId == itemId);
        }

        /// <summary>A narration item, Alice's line and a pause.</summary>
        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = Guid.NewGuid(), Name = "Alice" });
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                    .AddParagraph("p1", p => p.AddNarration("n1", "The door swung open."))
                    .AddParagraph("p2", p => p.AddCharacterLine("line", "“Hello there,” she said.", "alice"))
                    .AddParagraph("p3", p => p.AddPause("pause"))))
                .BuildAsync();
            return b;
        }

        private async Task GiveItemAudioAndReviewAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            (await db.ParagraphItems.FindAsync(itemId))!.AudioFileName = "line.wav";
            db.AudioReviews.Add(new AudioReview
            {
                Id = Guid.NewGuid(),
                ParagraphItemId = itemId,
                State = EntityReviewState.NeedsReview,
                NormalizeOk = true,
                VerifyOk = false,
                Wer = 0.5,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task A_cue_is_trimmed_replaced_and_cleared_by_null_empty_or_whitespace()
        {
            var line = (await SeedAsync()).ItemId("line");

            AssertOk(await SetCueAsync(line, "  softly  "));
            Assert.Equal("softly", await CueAsync(line));

            AssertOk(await SetCueAsync(line, "loud, angry"));
            Assert.Equal("loud, angry", await CueAsync(line));

            foreach (var clear in new[] { null, "", "   " })
            {
                AssertOk(await SetCueAsync(line, "whispered"));
                AssertOk(await SetCueAsync(line, clear));
                Assert.Null(await CueAsync(line));
            }
        }

        [Fact]
        public async Task Narration_takes_a_cue_as_well_as_dialog()
        {
            var narration = (await SeedAsync()).ItemId("n1");

            AssertOk(await SetCueAsync(narration, "slow and grave"));

            Assert.Equal("slow and grave", await CueAsync(narration));
        }

        [Fact]
        public async Task An_over_long_cue_a_pause_and_an_unknown_item_are_refused_with_a_422()
        {
            var b = await SeedAsync();
            var line = b.ItemId("line");

            AssertOk(await SetCueAsync(line, new string('a', 200)));
            Assert422(await SetCueAsync(line, new string('b', 201)));
            Assert.Equal(new string('a', 200), await CueAsync(line));

            Assert422(await SetCueAsync(Guid.NewGuid(), "softly"));

            Assert422(await SetCueAsync(b.ItemId("pause"), "softly"));
            Assert.Null(await CueAsync(b.ItemId("pause")));
        }

        [Fact]
        public async Task A_changed_cue_discards_audio_and_review_and_an_unchanged_one_keeps_them()
        {
            var line = (await SeedAsync()).ItemId("line");
            AssertOk(await SetCueAsync(line, "calm"));
            await GiveItemAudioAndReviewAsync(line);

            // The same cue, normalised, writes nothing: the take and its verdict survive.
            AssertOk(await SetCueAsync(line, " calm "));
            Assert.Equal("line.wav", (await ItemAsync(line)).AudioFileName);
            Assert.True(await HasReviewAsync(line));

            AssertOk(await SetCueAsync(line, "furious"));
            var item = await ItemAsync(line);
            Assert.Equal("furious", item.VoiceInstructions);
            Assert.Null(item.AudioFileName);
            Assert.False(await HasReviewAsync(line));
        }
    }
}
