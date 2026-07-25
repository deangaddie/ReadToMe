using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// Step-mechanic tests for <see cref="CharacterAttributionService"/> as an <see cref="IChainStep"/>:
    /// one config's run over items — the chunk pipeline, parse/classify, the unaskable pre-filter and
    /// the final-rung parse-failure fallback. Drives the coarse <c>RunAsync</c> seam directly (no chain
    /// lookup, no walk). A single paragraph is a chunk of 1, so these cases are chunk-size
    /// parameterized rather than split into single/batch pairs. Request construction is pinned
    /// directly in <see cref="AttributionRequestBuilderTests"/>; walk policy (escalation, best-prior,
    /// ModelLoading short-circuit, no-config) lives in <see cref="CharacterAttributionChainTests"/>.
    /// </summary>
    public class CharacterAttributionServiceTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");
        private static readonly Guid Chapter = Guid.NewGuid();

        private static QueuedParagraph Para(string preview) =>
            new(Folder, Guid.NewGuid(), preview, Chapter, Guid.NewGuid(), Guid.NewGuid());

        /// <summary>n paragraphs in one chapter, previewed P0..Pn-1.</summary>
        private static List<QueuedParagraph> Paras(int count) =>
            [.. Enumerable.Range(0, count).Select(i => Para($"P{i}"))];

        private LlmPromptService NewPrompts() =>
            new(Factory, NullLogger<LlmPromptService>.Instance);

        private CharacterAttributionService NewService(ILlmCompletionRunner runner, IProjectReader reader) =>
            new(runner, new AttributionRequestBuilder(NewPrompts(), reader),
                NullLogger<CharacterAttributionService>.Instance);

        /// <summary>A single in-memory config; RunAsync uses it directly (never the DB/settings).</summary>
        private static LlmServerConfig Config(int batchSize = 8) =>
            new() { Name = "Test", BaseUrl = "http://localhost:8080", Model = "test", AttributionBatchSize = batchSize };

        /// <summary>
        /// Drives the step over the items on one config (no self-consistency) and collects each item's
        /// outcome in yield order. Final by default, so the final-rung fallback is in play.
        /// </summary>
        private static async Task<List<(QueuedParagraph Item, AttributionOutcome Outcome)>> RunConfigAsync(
            CharacterAttributionService svc, LlmServerConfig config, IReadOnlyList<QueuedParagraph> items,
            bool thinking = false, bool isFinal = true, bool selfConsistency = false,
            CancellationToken ct = default)
        {
            var outcomes = new List<(QueuedParagraph, AttributionOutcome)>();
            await foreach (var (item, step) in ((IChainStep)svc).RunAsync(
                items, new ChainStepOptions(config, isFinal, selfConsistency, thinking),
                callbacks: null, ct))
                outcomes.Add((item, step.Outcome));
            return outcomes;
        }

        // ---------------------------------------------------------------
        // Fakes
        // ---------------------------------------------------------------

        /// <summary>
        /// Serves batch contexts the way the real reader does, since attribution now only ever uses
        /// the batch reader: a requested id with no text has no content item, so the reader returns
        /// null if it is the <em>first</em> id, and otherwise ends the leading contiguous run there
        /// and defers the remainder. <see cref="Defer"/> forces that same break for an id that does
        /// have text, modelling an intervening unattributed paragraph.
        /// </summary>
        private sealed class FakeProjectReader(
            IReadOnlyDictionary<Guid, string?> texts,
            Project? project = null,
            List<Character>? characters = null) : ProjectReaderFakeBase
        {
            private readonly List<Character> _characters = characters ?? [];

            /// <summary>Ids that end the run when they are not the first requested id.</summary>
            public HashSet<Guid> Defer { get; init; } = [];

            private string? TextOf(Guid id) => texts.TryGetValue(id, out var t) ? t : null;

            public override Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
                ProjectFolderId folderId, Guid chapterId, IReadOnlyList<Guid> paragraphIds, int before, int after)
            {
                if (paragraphIds.Count == 0 || TextOf(paragraphIds[0]) is null)
                    return Task.FromResult<ParagraphBatchContext?>(null);

                var included = new List<Guid> { paragraphIds[0] };
                var next = 1;
                while (next < paragraphIds.Count
                    && TextOf(paragraphIds[next]) is not null
                    && !Defer.Contains(paragraphIds[next]))
                {
                    included.Add(paragraphIds[next]);
                    next++;
                }

                var entries = included.Select((id, i) => new BatchContextEntry(TextOf(id)!, [], i)).ToList();
                return Task.FromResult<ParagraphBatchContext?>(
                    new ParagraphBatchContext(entries, included, [.. paragraphIds.Skip(next)]));
            }

            public override Task<Project?> GetProjectAsync(ProjectFolderId folderId) => Task.FromResult(project);
            public override Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId) => Task.FromResult(_characters);
            public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) => Task.FromResult(_characters);
        }

        private static Dictionary<Guid, string?> TextsFor(
            IReadOnlyList<QueuedParagraph> items, Func<int, string?> textFor) =>
            items.Select((p, i) => (p.ParagraphId, Text: textFor(i)))
                .ToDictionary(x => x.ParagraphId, x => x.Text);

        /// <summary>A reader over these items, item i reading <c>textFor(i)</c> (null = no content item).</summary>
        private static FakeProjectReader Reader(
            IReadOnlyList<QueuedParagraph> items, Func<int, string?> textFor, List<Character>? characters = null) =>
            new(TextsFor(items, textFor), DefaultProject(), characters);

        /// <summary>Every paragraph reads "Hello world" — the one-paragraph default.</summary>
        private static FakeProjectReader Reader(
            IReadOnlyList<QueuedParagraph> items, List<Character>? characters = null) =>
            Reader(items, _ => "Hello world", characters);

        /// <summary>Item i reads "Text i", so each index answers about its own text.</summary>
        private static FakeProjectReader IndexedReader(
            IReadOnlyList<QueuedParagraph> items, List<Character>? characters = null) =>
            Reader(items, i => $"Text {i}", characters);

        private static Project DefaultProject() =>
            new() { Id = Guid.NewGuid(), Title = "Book", BookTitle = "The Book", Author = "Author", Filename = "b.epub" };

        private static Character Character(string name) =>
            new() { Id = Guid.NewGuid(), Name = name, Aliases = [] };

        // ---------------------------------------------------------------
        // Segment answers. Segment texts must reconstruct the paragraph they answer
        // ("Hello world" for a chunk of 1, "Text N" for index N) — an answer that
        // does not is a fidelity failure, which is what ParseFailure means here.
        // ---------------------------------------------------------------

        private static string Segment(string text, string speaker, string type = "dialog", string voice = "") =>
            $$"""{ "text": {{JsonSerializer.Serialize(text)}}, "type": "{{type}}", "speaker": "{{speaker}}", "voice_instructions": "{{voice}}" }""";

        /// <summary>Object-shaped answer covering the whole of "Hello world".</summary>
        private static string Answer(string speaker, string voice = "", string text = "Hello world") =>
            $$"""{ "reasoning": "r", "segments": [ {{Segment(text, speaker, voice: voice)}} ] }""";

        /// <summary>Array-shaped answer: index i speaks the whole of its own paragraph text.</summary>
        private static string BatchAnswer(params (int Index, string Speaker)[] entries) =>
            "[" + string.Join(",", entries.Select(e =>
                $$"""{ "index": {{e.Index}}, "reasoning": "r", "segments": [ {{Segment($"Text {e.Index}", e.Speaker)}} ] }""")) + "]";

        // ---------------------------------------------------------------
        // Chunk of 1 (object-shaped ask)
        // ---------------------------------------------------------------

        [Fact]
        public async Task ValidSegmentResponse_ReturnsResolved_WithSegments()
        {
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner().Completes(Answer("Alice", "calm"));
            var svc = NewService(runner, Reader(items));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            var segment = Assert.Single(result.Segments!);
            Assert.Equal("Hello world", segment.Text);
            Assert.Equal(AttributionSegmentType.Dialog, segment.Type);
            Assert.Equal("Alice", segment.Speaker);
            Assert.Equal("calm", segment.VoiceInstructions);

            // One ask for the chunk. What that ask looks like (label, schema, shape, thinking) is
            // pinned directly in AttributionRequestBuilderTests.
            Assert.Single(runner.Requests);
        }

        [Fact]
        public async Task MultiSpeakerAnswer_ReturnsEverySegment_SlicedFromTheOriginal()
        {
            const string text = "\"Hello,\" she said. \"Goodbye.\"";
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner().Completes($$"""
                { "reasoning": "r", "segments": [
                    {{Segment("\"Hello,\"", "Alice")}},
                    {{Segment("she said.", "narrator", type: "narration")}},
                    {{Segment("\"Goodbye.\"", "Bob")}} ] }
                """);
            var svc = NewService(runner, Reader(items, _ => text, [Character("Alice"), Character("Bob")]));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal(3, result.Segments!.Count);
            // The slices concatenate back to the original text, verbatim.
            Assert.Equal(text, string.Concat(result.Segments.Select(s => s.Text)));
            Assert.Equal(AttributionSegmentType.Narration, result.Segments[1].Type);
        }

        [Fact]
        public async Task LlmReturnsUnknownSpeaker_ReturnsUnknownStatus_StillCarryingSegments()
        {
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner().Completes(Answer("unknown"));
            var svc = NewService(runner, Reader(items));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            // The answer still applies: the segmentation is real even when the speaker is not known.
            Assert.Single(result.Segments!);
        }

        [Fact]
        public async Task SegmentsDoNotReconstructParagraph_ReturnsFailed_WithoutAReAsk()
        {
            // The model dropped a word — a fidelity failure, which escalates like a parse failure.
            // The final-rung fallback answers an unparseable *run*, not this, so the answer stands
            // on one ask even on the final rung.
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner().Completes(Answer("Alice", text: "Hello"));
            var svc = NewService(runner, Reader(items));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Null(result.Segments);
            Assert.Single(runner.Requests);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task RunFailed_ReturnsFailed_ForEveryItem(int count)
        {
            var items = Paras(count);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.Failed, "connection refused");
            var svc = NewService(runner, IndexedReader(items));

            var results = await RunConfigAsync(svc, Config(), items);

            Assert.Equal(count, results.Count);
            Assert.All(results, r =>
            {
                Assert.Equal(AttributionStatus.Failed, r.Outcome.Status);
                Assert.Contains("connection refused", r.Outcome.FailureReason);
            });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task ServiceUnavailableRun_ReturnsServiceUnavailable_ForEveryItem(int count)
        {
            var items = Paras(count);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.ServiceUnavailable, "stalled");
            var svc = NewService(runner, IndexedReader(items));

            var results = await RunConfigAsync(svc, Config(), items);

            Assert.Equal(count, results.Count);
            Assert.All(results, r => Assert.Equal(AttributionStatus.ServiceUnavailable, r.Outcome.Status));
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner().Throws(new OperationCanceledException());
            var svc = NewService(runner, Reader(items));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in ((IChainStep)svc).RunAsync(
                    items, new ChainStepOptions(Config(), IsFinal: true, SelfConsistency: false),
                    callbacks: null, cts.Token))
                {
                }
            });
        }

        // ---------------------------------------------------------------
        // Unaskable pre-filter: nothing to attribute, so no LLM call at any chunk size
        // ---------------------------------------------------------------

        [Fact]
        public async Task BlankQueryText_ReturnsUnknown_WithoutRunning()
        {
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner();
            var svc = NewService(runner, Reader(items, _ => "   "));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task NullContext_ReturnsUnknown_WithoutRunning()
        {
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner();
            // No content item for the paragraph: the reader has nothing to window on and returns null.
            var svc = NewService(runner, Reader(items, _ => null));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Batch_BlankTarget_IsUnknown_AndTheRestAreStillAsked()
        {
            // The batch-shaped counterpart: a blank target is demoted out of the prompt rather than
            // dragging the whole chunk into an alignment failure.
            var items = Paras(3);
            var runner = new FakeLlmCompletionRunner().Completes(BatchAnswer((0, "Alice"), (1, "Bob")));
            var svc = NewService(runner, Reader(items, i => i switch
            {
                0 => "Text 0",
                1 => "   ",
                _ => "Text 1",
            }));

            var results = await RunConfigAsync(svc, Config(), items);

            Assert.Single(runner.Requests);
            Assert.Equal(3, results.Count);
            // Book order is preserved, and the blank one is Unknown without ever being asked about.
            Assert.Equal(items.Select(i => i.ParagraphId), results.Select(r => r.Item.ParagraphId));
            Assert.Equal(AttributionStatus.Resolved, results[0].Outcome.Status);
            Assert.Equal(AttributionStatus.Unknown, results[1].Outcome.Status);
            Assert.Null(results[1].Outcome.Segments);
            Assert.Equal(AttributionStatus.Resolved, results[2].Outcome.Status);
        }

        [Fact]
        public async Task Batch_MissingFirstParagraph_IsUnknown_AndTheRestAreStillAsked()
        {
            // The reader returns null when the *first* requested id has no content item; the builder
            // bins it Unaskable and re-asks for the rest, instead of falling back to per-item asks.
            var items = Paras(3);
            var runner = new FakeLlmCompletionRunner().Completes(BatchAnswer((0, "Alice"), (1, "Bob")));
            var svc = NewService(runner, Reader(items, i => i == 0 ? null : $"Text {i - 1}"));

            var results = await RunConfigAsync(svc, Config(), items);

            Assert.Single(runner.Requests);
            Assert.Equal(AttributionStatus.Unknown, results[0].Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, results[1].Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, results[2].Outcome.Status);
        }

        // ---------------------------------------------------------------
        // Chunk of many (array-shaped ask)
        // ---------------------------------------------------------------

        [Fact]
        public async Task Batch_ValidResponse_ResolvesEachIndex_SingleRun()
        {
            var items = Paras(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "unknown"), (2, "Bob")));
            var svc = NewService(runner, IndexedReader(items));

            var result = await RunConfigAsync(svc, Config(), items);

            Assert.Equal(3, result.Count);
            Assert.Equal(items[0], result[0].Item);
            Assert.Equal(AttributionStatus.Resolved, result[0].Outcome.Status);
            // Each index's segments are sliced from that index's own paragraph text.
            Assert.Equal("Text 0", Assert.Single(result[0].Outcome.Segments!).Text);
            Assert.Equal("Alice", result[0].Outcome.Segments![0].Speaker);
            Assert.Equal(AttributionStatus.Unknown, result[1].Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, result[2].Outcome.Status);
            Assert.Equal("Text 2", Assert.Single(result[2].Outcome.Segments!).Text);

            // One run for the whole chunk — the batch size covers all 3, so they are not re-chunked.
            Assert.Single(runner.Requests);
        }

        [Fact]
        public async Task Batch_ExtraUnrequestedIndex_IsIgnored()
        {
            var items = Paras(2);
            // Models also answer for context paragraphs; indexes nobody asked for are dropped.
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "Bob"), (7, "Nobody")));
            var svc = NewService(runner, IndexedReader(items));

            var result = await RunConfigAsync(svc, Config(), items);

            Assert.Single(runner.Requests);
            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task ContextDeferral_ReAsksTheTrimmedItems_InALaterChunk()
        {
            var items = Paras(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice", text: "Text 0"))
                .Completes(Answer("Alice", text: "Text 1"));
            var reader = new FakeProjectReader(
                TextsFor(items, i => $"Text {i}"), DefaultProject())
            {
                Defer = [items[1].ParagraphId],
            };
            var svc = NewService(runner, reader);

            var results = await RunConfigAsync(svc, Config(), items);

            // Chunk 1 asks about P0 only and defers P1; the group's pending loop then asks P1 alone,
            // where it is the first requested id and so is included.
            Assert.Equal(2, results.Count);
            Assert.Equal(2, runner.Requests.Count);
            Assert.All(runner.Requests, r => Assert.Equal(CompletionShape.Object, r.Shape));
        }

        // ---------------------------------------------------------------
        // Final-rung parse-failure fallback (D6/D14): 2 asks per paragraph, at most
        // ---------------------------------------------------------------

        [Fact]
        public async Task Final_ChunkParseFailure_ReAsksEachParagraphOnItsOwn()
        {
            var items = Paras(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes("not json at all")
                .Completes(Answer("Alice", text: "Text 0"))
                .Completes(Answer("Alice", text: "Text 1"));
            var svc = NewService(runner, IndexedReader(items));

            var result = await RunConfigAsync(svc, Config(), items);

            // 1 chunk run + 2 single re-asks.
            Assert.Equal(3, runner.Requests.Count);
            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
            // The re-asks are object-shaped: the template follows the included count, so a fallback
            // single gets the single prompt for free by re-entering the same pipeline.
            Assert.All(runner.Requests.Skip(1), r => Assert.Equal(CompletionShape.Object, r.Shape));
        }

        [Fact]
        public async Task Final_MissingIndex_ReAsksTheWholeChunkAsSingles()
        {
            // Escalation's unit is the paragraph, so a chunk answer that skips a requested index is
            // rejected whole — every included paragraph is re-asked, not just the missing one.
            var items = Paras(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (2, "Bob")))
                .Completes(Answer("Fallback", text: "Text 0"))
                .Completes(Answer("Fallback", text: "Text 1"))
                .Completes(Answer("Fallback", text: "Text 2"));
            var svc = NewService(runner, IndexedReader(items));

            var result = await RunConfigAsync(svc, Config(), items);

            // 1 chunk run + 3 single re-asks.
            Assert.Equal(4, runner.Requests.Count);
            Assert.All(result, o =>
                Assert.Equal("Fallback", Assert.Single(o.Outcome.Segments!).Speaker));
        }

        [Fact]
        public async Task Final_SingleParseFailure_IsReAskedOnce_OffGreedy()
        {
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner()
                .Completes("not json at all")
                .Completes(Answer("Alice"));
            var svc = NewService(runner, Reader(items));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal(2, runner.Requests.Count);
            // A greedy re-ask of an identical prompt would return the identical garbage.
            Assert.Null(runner.Requests[0].Overrides!.Temperature);
            Assert.True(runner.Requests[1].Overrides!.Temperature > 0);
        }

        [Fact]
        public async Task Final_SingleParseFailure_ReAskAlsoFails_StopsAtTwoAsks()
        {
            var items = Paras(1);
            var runner = new FakeLlmCompletionRunner().Completes("not json at all");
            var svc = NewService(runner, Reader(items));

            var result = Assert.Single(await RunConfigAsync(svc, Config(), items)).Outcome;

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Equal(2, runner.Requests.Count);
        }

        [Fact]
        public async Task Final_ChunkParseFailure_SinglesThatAlsoFail_AreNotReAskedAgain()
        {
            // Each fallback single is already its paragraph's second ask, so it terminates there:
            // 1 chunk + 2 singles = 3, never 5.
            var items = Paras(2);
            var runner = new FakeLlmCompletionRunner().Completes("not json at all");
            var svc = NewService(runner, IndexedReader(items));

            var result = await RunConfigAsync(svc, Config(), items);

            Assert.Equal(3, runner.Requests.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Failed, o.Outcome.Status));
        }

        // ---------------------------------------------------------------
        // Self-consistency: the sample-1 guard is about the run, not one item's answer
        // ---------------------------------------------------------------

        [Fact]
        public async Task SelfConsistency_OneMisalignedItem_StillTakesSample2_ForTheRest()
        {
            // Index 1's segments do not reconstruct its paragraph — a per-item fidelity failure, not
            // an unparseable run. The chunk-wide guard must not read it as one and skip sample 2,
            // which would silently drop the check for every other paragraph in the chunk.
            var items = Paras(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes("""
                    [ { "index": 0, "reasoning": "r", "segments": [
                          { "text": "Text 0", "type": "dialog", "speaker": "Alice", "voice_instructions": "" } ] },
                      { "index": 1, "reasoning": "r", "segments": [
                          { "text": "Something else", "type": "dialog", "speaker": "Bob", "voice_instructions": "" } ] } ]
                    """)
                .Completes(BatchAnswer((0, "Alice"), (1, "Bob")));
            var svc = NewService(runner, IndexedReader(items, [Character("Alice"), Character("Bob")]));

            var results = await RunConfigAsync(svc, Config(), items, isFinal: false, selfConsistency: true);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal(AttributionStatus.Resolved, results[0].Outcome.Status);
            Assert.Equal(AttributionStatus.Failed, results[1].Outcome.Status);   // sample 1 stands
        }

        [Fact]
        public async Task SelfConsistency_UnparseableRun_MakesOneCall_NotTwo()
        {
            var items = Paras(2);
            var runner = new FakeLlmCompletionRunner().Completes("not json at all");
            var svc = NewService(runner, IndexedReader(items));

            var results = await RunConfigAsync(svc, Config(), items, isFinal: false, selfConsistency: true);

            // The one call's outcome covers the whole chunk; a second sample cannot improve on it.
            Assert.Single(runner.Requests);
            Assert.All(results, r => Assert.Equal(AttributionStatus.Failed, r.Outcome.Status));
        }

        [Fact]
        public async Task NonFinal_ParseFailure_IsNotReAsked()
        {
            // Non-final rungs hand the chunk on as ParseFailure suspects; the walk owns the retry.
            var items = Paras(2);
            var runner = new FakeLlmCompletionRunner().Completes("not json at all");
            var svc = NewService(runner, IndexedReader(items));

            var result = await RunConfigAsync(svc, Config(), items, isFinal: false);

            Assert.Single(runner.Requests);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Failed, o.Outcome.Status));
        }
    }
}
