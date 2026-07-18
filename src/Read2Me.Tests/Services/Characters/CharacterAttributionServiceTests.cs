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
    /// one config's run over items — prompt building, parse/classify, batch core, and the batch→single
    /// fallback. Drives the coarse <c>RunAsync</c> seam directly (no chain lookup, no walk). Walk policy
    /// (escalation, best-prior, ModelLoading short-circuit, no-config) lives in
    /// <see cref="CharacterAttributionChainTests"/>.
    /// </summary>
    public class CharacterAttributionServiceTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static readonly QueuedParagraph TestItem = new(
            Folder,
            Guid.NewGuid(),
            "Preview",
            Guid.NewGuid(),  // chapterId
            Guid.NewGuid(),
            Guid.NewGuid());

        private LlmPromptService NewPrompts() =>
            new(Factory, NullLogger<LlmPromptService>.Instance);

        private CharacterAttributionService NewService(ILlmCompletionRunner runner, IProjectReader reader,
            LlmPromptService? prompts = null) =>
            new(runner, prompts ?? NewPrompts(), reader,
                NullLogger<CharacterAttributionService>.Instance);

        /// <summary>A single in-memory config; RunAsync uses it directly (never the DB/settings).</summary>
        private static LlmServerConfig Config(int batchSize = 8) =>
            new() { Name = "Test", BaseUrl = "http://localhost:8080", Model = "test", AttributionBatchSize = batchSize };

        /// <summary>
        /// Drives the step over the items as the final config (no self-consistency) and collects each
        /// item's outcome in yield order.
        /// </summary>
        private static async Task<List<(QueuedParagraph Item, AttributionOutcome Outcome)>> RunConfigAsync(
            CharacterAttributionService svc, LlmServerConfig config, IReadOnlyList<QueuedParagraph> items,
            CancellationToken ct = default)
        {
            var outcomes = new List<(QueuedParagraph, AttributionOutcome)>();
            await foreach (var (item, step) in ((IChainStep)svc).RunAsync(
                items, new ChainStepOptions(config, IsFinal: true, SelfConsistency: false), callbacks: null, ct))
                outcomes.Add((item, step.Outcome));
            return outcomes;
        }

        private static async Task<AttributionOutcome> RunSingleAsync(
            CharacterAttributionService svc, LlmServerConfig config, QueuedParagraph item) =>
            Assert.Single(await RunConfigAsync(svc, config, [item])).Outcome;

        // ---------------------------------------------------------------
        // Fakes
        // ---------------------------------------------------------------

        private sealed class FakeProjectReader : IProjectReader
        {
            public int ReceivedBefore { get; private set; }
            public int ReceivedAfter { get; private set; }
            private readonly ParagraphContext? _context;
            private readonly Project? _project;
            private readonly List<Character> _characters;

            public FakeProjectReader(
                ParagraphContext? context = null,
                Project? project = null,
                List<Character>? characters = null)
            {
                _context = context;
                _project = project;
                _characters = characters ?? [];
            }

            public Task<ParagraphContext?> GetParagraphContextAsync(
                ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after)
            {
                ReceivedBefore = before;
                ReceivedAfter = after;
                return Task.FromResult(_context);
            }

            public ParagraphBatchContext? BatchContext { get; set; }
            public IReadOnlyList<Guid>? ReceivedBatchIds { get; private set; }

            public Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
                ProjectFolderId folderId, Guid chapterId, IReadOnlyList<Guid> paragraphIds, int before, int after)
            {
                ReceivedBefore = before;
                ReceivedAfter = after;
                ReceivedBatchIds = paragraphIds;
                return Task.FromResult(BatchContext);
            }

            public Task<Data.Entities.Project?> GetProjectAsync(ProjectFolderId folderId)
                => Task.FromResult(_project);

            public Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId)
                => Task.FromResult(_characters);

            public Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId)
                => Task.FromResult(_characters);

            public Task<List<Read2Me.Core.Models.CharacterLine>> GetCharacterLinesAsync(ProjectFolderId folderId, Guid characterId)
                => Task.FromResult(new List<Read2Me.Core.Models.CharacterLine>());

            // Unused members — not under test
            public Task<BookOverview> GetBookOverviewAsync(ProjectFolderId folderId) =>
                Task.FromResult(new BookOverview(null, false, [], [], 0, 0, [], new System.Collections.Generic.Dictionary<System.Guid, int>()));
            public IReadOnlyList<string> GetProjects() => [];
            public Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync() => Task.FromResult<IReadOnlyList<ProjectSummary>>([]);
            public Task<bool> HasBookContentAsync(ProjectFolderId folderId) => Task.FromResult(false);
            public Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId) => Task.FromResult(new List<Volume>());
            public Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId) => Task.FromResult(new List<Part>());
            public Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId) => Task.FromResult(new List<Chapter>());
            public Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId) => Task.FromResult(new List<Paragraph>());
            public Task<int> GetTotalPartCountAsync(ProjectFolderId folderId) => Task.FromResult(0);
            public Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId) => Task.FromResult(0);
            public Task<List<CharacterParagraphRef>> GetCharacterParagraphsAsync(
                ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = false)
                => Task.FromResult(new List<CharacterParagraphRef>());
            public Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId) => Task.FromResult(new HashSet<Guid>());
            public Task<int> CountUnattributedCharacterItemsAsync(ProjectFolderId folderId, Guid paragraphId) => Task.FromResult(0);
            public Task<List<(Guid ParagraphId, string Preview)>> GetOrderedParagraphsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphIds) => Task.FromResult(new List<(Guid, string)>());
            public Task<List<Read2Me.Data.Entities.Voice>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId) => Task.FromResult(new List<Read2Me.Data.Entities.Voice>());
            public Task<Read2Me.Data.Entities.Voice?> GetVoiceAsync(ProjectFolderId folderId, Guid voiceId) => Task.FromResult<Read2Me.Data.Entities.Voice?>(null);
            public Task<Guid?> GetDefaultVoiceIdAsync(ProjectFolderId folderId, Guid characterId) => Task.FromResult<Guid?>(null);
            public Task<HierarchyChildren> GetChildrenAsync(ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId)
                => Task.FromResult(new HierarchyChildren(null, null, null));
            public Task<List<Read2Me.Core.Models.AudioItemRef>> GetAudioItemRefsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool needsAudioOnly = false, bool narratorOnlyMode = false)
                => Task.FromResult(new List<Read2Me.Core.Models.AudioItemRef>());
            public Task<IReadOnlyDictionary<Guid, int>> GetNodeAudioItemCountsAsync(ProjectFolderId folderId)
                => Task.FromResult<IReadOnlyDictionary<Guid, int>>(new System.Collections.Generic.Dictionary<Guid, int>());
            public Task<List<Read2Me.Core.Models.AudioItemRef>> GetOrderedAudioItemRefsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphItemIds)
                => Task.FromResult(new List<Read2Me.Core.Models.AudioItemRef>());
            public Task<List<(Guid ParagraphItemId, Read2Me.Services.Audio.AudioReviewInfo Info)>> GetAudioReviewsAsync(ProjectFolderId folderId)
                => Task.FromResult(new List<(Guid, Read2Me.Services.Audio.AudioReviewInfo)>());
            public Task<IReadOnlyList<Read2Me.Services.NodeStatus.ParagraphStatusSeedRow>> GetNodeStatusSeedAsync(ProjectFolderId folderId)
                => Task.FromResult<IReadOnlyList<Read2Me.Services.NodeStatus.ParagraphStatusSeedRow>>([]);
            public Task<IReadOnlyList<AssemblyManifestEntry>> GetAssemblyManifestAsync(ProjectFolderId folder, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<AssemblyManifestEntry>>([]);
            public Task<IReadOnlyList<AudioSampleInfo>> GetAudioSampleInfosAsync(ProjectFolderId folderId, IReadOnlyCollection<Guid> itemIds)
                => Task.FromResult<IReadOnlyList<AudioSampleInfo>>([]);
            public Task<List<VoiceRuleRow>> GetCharacterVoiceRulesAsync(ProjectFolderId folderId, Guid characterId)
                => Task.FromResult(new List<VoiceRuleRow>());
        }

        private static ParagraphContext DefaultContext() =>
            new(new ContextParagraph("Hello world", []), [], []);

        private static Project DefaultProject() =>
            new() { Id = Guid.NewGuid(), Title = "Book", BookTitle = "The Book", Author = "Author", Filename = "b.epub" };

        private static Character Character(string name) =>
            new() { Id = Guid.NewGuid(), Name = name, Aliases = [] };

        // ---------------------------------------------------------------
        // Segment answers. Segment texts must reconstruct the paragraph they answer
        // ("Hello world" for the single path, "Text N" for batch index N) — an answer that
        // does not is a fidelity failure, which is what ParseFailure means here.
        // ---------------------------------------------------------------

        private static string Segment(string text, string speaker, string type = "dialog", string voice = "") =>
            $$"""{ "text": {{JsonSerializer.Serialize(text)}}, "type": "{{type}}", "speaker": "{{speaker}}", "voice_instructions": "{{voice}}" }""";

        /// <summary>Single-paragraph answer covering the whole of "Hello world".</summary>
        private static string Answer(string speaker, string voice = "", string text = "Hello world") =>
            $$"""{ "reasoning": "r", "segments": [ {{Segment(text, speaker, voice: voice)}} ] }""";

        /// <summary>Batch answer: index i speaks the whole of its own paragraph text.</summary>
        private static string BatchAnswer(params (int Index, string Speaker)[] entries) =>
            "[" + string.Join(",", entries.Select(e =>
                $$"""{ "index": {{e.Index}}, "reasoning": "r", "segments": [ {{Segment($"Text {e.Index}", e.Speaker)}} ] }""")) + "]";

        // ---------------------------------------------------------------
        // Single-paragraph step
        // ---------------------------------------------------------------

        [Fact]
        public async Task ValidSegmentResponse_ReturnsResolved_WithSegments()
        {
            var runner = new FakeLlmCompletionRunner().Completes(Answer("Alice", "calm"));
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            var segment = Assert.Single(result.Segments!);
            Assert.Equal("Hello world", segment.Text);
            Assert.Equal(AttributionSegmentType.Dialog, segment.Type);
            Assert.Equal("Alice", segment.Speaker);
            Assert.Equal("calm", segment.VoiceInstructions);

            // Single path runs a schema-constrained object completion labelled with the preview.
            var request = Assert.Single(runner.Requests);
            Assert.Equal("Preview", request.Label);
            Assert.Equal(CompletionShape.Object, request.Shape);
            Assert.Equal(SegmentAttributionSchema.JsonSchema, request.JsonSchema);
        }

        [Fact]
        public async Task MultiSpeakerAnswer_ReturnsEverySegment_SlicedFromTheOriginal()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("\"Hello,\" she said. \"Goodbye.\"", []), [], []);
            var runner = new FakeLlmCompletionRunner().Completes($$"""
                { "reasoning": "r", "segments": [
                    {{Segment("\"Hello,\"", "Alice")}},
                    {{Segment("she said.", "narrator", type: "narration")}},
                    {{Segment("\"Goodbye.\"", "Bob")}} ] }
                """);
            var svc = NewService(runner, new FakeProjectReader(ctx, DefaultProject(),
                [Character("Alice"), Character("Bob")]));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal(3, result.Segments!.Count);
            // The slices concatenate back to the original text, verbatim.
            Assert.Equal(ctx.Query.Text, string.Concat(result.Segments.Select(s => s.Text)));
            Assert.Equal(AttributionSegmentType.Narration, result.Segments[1].Type);
        }

        [Fact]
        public async Task LlmReturnsUnknownSpeaker_ReturnsUnknownStatus_StillCarryingSegments()
        {
            var runner = new FakeLlmCompletionRunner().Completes(Answer("unknown"));
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            // The answer still applies: the segmentation is real even when the speaker is not known.
            Assert.Single(result.Segments!);
        }

        [Fact]
        public async Task SegmentsDoNotReconstructParagraph_ReturnsFailed()
        {
            // The model dropped a word — a fidelity failure, which escalates like a parse failure.
            var runner = new FakeLlmCompletionRunner().Completes(Answer("Alice", text: "Hello"));
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Null(result.Segments);
        }

        [Fact]
        public async Task RunFailed_ReturnsFailed()
        {
            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.Failed, "connection refused");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Contains("connection refused", result.FailureReason);
        }

        [Fact]
        public async Task ServiceUnavailableRun_ReturnsServiceUnavailable()
        {
            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.ServiceUnavailable, "stalled");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.ServiceUnavailable, result.Status);
        }

        [Fact]
        public async Task UnparseableResponse_ReturnsFailed()
        {
            var runner = new FakeLlmCompletionRunner().Completes("This is not JSON at all.");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Failed, result.Status);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var runner = new FakeLlmCompletionRunner().Throws(new OperationCanceledException());
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in ((IChainStep)svc).RunAsync(
                    [TestItem], new ChainStepOptions(Config(), IsFinal: true, SelfConsistency: false),
                    callbacks: null, cts.Token))
                {
                }
            });
        }

        [Fact]
        public async Task BlankQueryText_ReturnsUnknown_WithoutRunning()
        {
            var runner = new FakeLlmCompletionRunner();
            var ctx = new ParagraphContext(new ContextParagraph("   ", []), [], []);
            var svc = NewService(runner, new FakeProjectReader(ctx, DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task NullContext_ReturnsUnknown_WithoutRunning()
        {
            var runner = new FakeLlmCompletionRunner();
            var svc = NewService(runner, new FakeProjectReader(null, DefaultProject()));

            var result = await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task ContextWindowDefaults_PassedToReader()
        {
            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var fakeReader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, fakeReader);

            await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(PromptTemplates.DefaultContextParagraphsBefore, fakeReader.ReceivedBefore);
            Assert.Equal(PromptTemplates.DefaultContextParagraphsAfter, fakeReader.ReceivedAfter);
        }

        [Fact]
        public async Task CustomContextWindow_PassedToReader()
        {
            var prompts = NewPrompts();
            await prompts.SetContextWindowAsync(7, 3);

            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var fakeReader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, fakeReader, prompts);

            await RunSingleAsync(svc, Config(), TestItem);

            Assert.Equal(7, fakeReader.ReceivedBefore);
            Assert.Equal(3, fakeReader.ReceivedAfter);
        }

        // ---------------------------------------------------------------
        // Batch step (batch core)
        // ---------------------------------------------------------------

        private static (List<QueuedParagraph> Batch, ParagraphBatchContext Ctx) MakeBatch(int count)
        {
            var chapterId = Guid.NewGuid();
            var batch = Enumerable.Range(0, count)
                .Select(i => new QueuedParagraph(Folder, Guid.NewGuid(), $"P{i}", chapterId, Guid.NewGuid(), Guid.NewGuid()))
                .ToList();
            var entries = batch
                .Select((_, i) => new BatchContextEntry($"Text {i}", [], i))
                .ToList();
            var ctx = new ParagraphBatchContext(
                entries, [.. batch.Select(b => b.ParagraphId)], []);
            return (batch, ctx);
        }

        [Fact]
        public async Task Batch_ValidResponse_ResolvesEachIndex_SingleRun()
        {
            var (batch, ctx) = MakeBatch(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "unknown"), (2, "Bob")));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), batch);

            Assert.Equal(3, result.Count);
            Assert.Equal(batch[0], result[0].Item);
            Assert.Equal(AttributionStatus.Resolved, result[0].Outcome.Status);
            // Each index's segments are sliced from that index's own paragraph text.
            Assert.Equal("Text 0", Assert.Single(result[0].Outcome.Segments!).Text);
            Assert.Equal("Alice", result[0].Outcome.Segments![0].Speaker);
            Assert.Equal(AttributionStatus.Unknown, result[1].Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, result[2].Outcome.Status);
            Assert.Equal("Text 2", Assert.Single(result[2].Outcome.Segments!).Text);

            // One array-shaped, schema-constrained run for the whole batch (batch size covers all 3).
            var request = Assert.Single(runner.Requests);
            Assert.Equal("3 paragraphs: P0", request.Label);
            Assert.Equal(CompletionShape.Array, request.Shape);
            Assert.Equal(SegmentBatchAttributionSchema.JsonSchema, request.JsonSchema);
        }

        [Fact]
        public async Task Batch_ExtraUnrequestedIndex_IsIgnored()
        {
            var (batch, ctx) = MakeBatch(2);
            // Models also answer for context paragraphs; indexes nobody asked for are dropped.
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "Bob"), (7, "Nobody")));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), batch);

            Assert.Single(runner.Requests);
            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_OfOne_UsesSinglePath()
        {
            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice", "calm"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), [TestItem]);

            var (item, outcome) = Assert.Single(result);
            Assert.Equal(TestItem, item);
            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal("Alice", Assert.Single(outcome.Segments!).Speaker);
            // Single path — the single-paragraph reader method was used, not the batch one.
            Assert.Null(reader.ReceivedBatchIds);
        }

        [Fact]
        public async Task Batch_UnparseableResponse_FallsBackToSinglePerItem()
        {
            var (batch, ctx) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes("not json at all")
                .Completes(Answer("Alice"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), batch);

            // 1 batch run + 2 single fallbacks (final step falls back to the single path per item).
            Assert.Equal(3, runner.Requests.Count);
            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_MissingIndex_FallsBackToSingleForTheWholeChunk()
        {
            // Escalation's unit is the paragraph, so a batch answer that skips a requested index is
            // rejected whole — the chunk falls back to the single path, not just the missing item.
            var (batch, ctx) = MakeBatch(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (2, "Bob")))
                .Completes(Answer("Fallback"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), batch);

            // 1 batch run + 3 single fallbacks.
            Assert.Equal(4, runner.Requests.Count);
            Assert.All(result, o =>
                Assert.Equal("Fallback", Assert.Single(o.Outcome.Segments!).Speaker));
        }

        [Fact]
        public async Task Batch_NullContext_FallsBackToSinglePerItem()
        {
            var (batch, _) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = null };
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), batch);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_ServiceUnavailableRun_AllItemsServiceUnavailable()
        {
            var (batch, ctx) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.ServiceUnavailable, "stalled");
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader);

            var result = await RunConfigAsync(svc, Config(), batch);

            Assert.Equal(2, result.Count);
            Assert.All(result, o => Assert.Equal(AttributionStatus.ServiceUnavailable, o.Outcome.Status));
        }
    }
}
