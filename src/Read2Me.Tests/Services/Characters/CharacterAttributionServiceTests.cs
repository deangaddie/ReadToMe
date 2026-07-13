using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
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

        private LlmSettingsService NewSettings() =>
            new(Factory, NullLogger<LlmSettingsService>.Instance);

        private LlmPromptService NewPrompts() =>
            new(Factory, NullLogger<LlmPromptService>.Instance);

        private CharacterAttributionService NewService(ILlmCompletionRunner runner, IProjectReader reader,
            LlmSettingsService? settings = null, LlmPromptService? prompts = null) =>
            new(runner, settings ?? NewSettings(), prompts ?? NewPrompts(), reader,
                NullLogger<CharacterAttributionService>.Instance,
                new EventBroadcaster<LlmStreamEvent>());

        private static async Task<LlmServerConfig> RegisterActiveConfigAsync(LlmSettingsService svc)
        {
            var config = new LlmServerConfig { Name = "Test", BaseUrl = "http://localhost:8080", Model = "test" };
            config = await svc.CreateConfigAsync(config);
            await svc.SetActiveConfigAsync(config.Id);
            return config;
        }

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
        // Tests
        // ---------------------------------------------------------------

        [Fact]
        public async Task NoActiveConfig_ReturnsNoLlmConfigured_WithoutRunning()
        {
            var runner = new FakeLlmCompletionRunner();
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.NoLlmConfigured, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task ValidSegmentResponse_ReturnsResolved_WithSegments()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner().Completes(Answer("Alice", "calm"));
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

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
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var ctx = new ParagraphContext(
                new ContextParagraph("\"Hello,\" she said. \"Goodbye.\"", []), [], []);
            var runner = new FakeLlmCompletionRunner().Completes($$"""
                { "reasoning": "r", "segments": [
                    {{Segment("\"Hello,\"", "Alice")}},
                    {{Segment("she said.", "narrator", type: "narration")}},
                    {{Segment("\"Goodbye.\"", "Bob")}} ] }
                """);
            var svc = NewService(runner, new FakeProjectReader(ctx, DefaultProject(),
                [Character("Alice"), Character("Bob")]), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal(3, result.Segments!.Count);
            // The slices concatenate back to the original text, verbatim.
            Assert.Equal(ctx.Query.Text, string.Concat(result.Segments.Select(s => s.Text)));
            Assert.Equal(AttributionSegmentType.Narration, result.Segments[1].Type);
        }

        [Fact]
        public async Task LlmReturnsUnknownSpeaker_ReturnsUnknownStatus_StillCarryingSegments()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner().Completes(Answer("unknown"));
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            // The answer still applies: the segmentation is real even when the speaker is not known.
            Assert.Single(result.Segments!);
        }

        [Fact]
        public async Task SegmentsDoNotReconstructParagraph_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            // The model dropped a word — a fidelity failure, which escalates like a parse failure.
            var runner = new FakeLlmCompletionRunner().Completes(Answer("Alice", text: "Hello"));
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Null(result.Segments);
        }

        [Fact]
        public async Task RunFailed_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.Failed, "connection refused");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Contains("connection refused", result.FailureReason);
        }

        [Fact]
        public async Task ServiceUnavailableRun_ReturnsServiceUnavailable()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.ServiceUnavailable, "stalled");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.ServiceUnavailable, result.Status);
        }

        [Fact]
        public async Task UnparseableResponse_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner().Completes("This is not JSON at all.");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Failed, result.Status);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var runner = new FakeLlmCompletionRunner().Throws(new OperationCanceledException());
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => svc.AttributeAsync(TestItem, cts.Token));
        }

        [Fact]
        public async Task BlankQueryText_ReturnsUnknown_WithoutRunning()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner();
            var ctx = new ParagraphContext(new ContextParagraph("   ", []), [], []);
            var svc = NewService(runner, new FakeProjectReader(ctx, DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task NullContext_ReturnsUnknown_WithoutRunning()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner();
            var svc = NewService(runner, new FakeProjectReader(null, DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task ContextWindowDefaults_PassedToReader()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var fakeReader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, fakeReader, settings);

            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(PromptTemplates.DefaultContextParagraphsBefore, fakeReader.ReceivedBefore);
            Assert.Equal(PromptTemplates.DefaultContextParagraphsAfter, fakeReader.ReceivedAfter);
        }

        [Fact]
        public async Task CustomContextWindow_PassedToReader()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var prompts = NewPrompts();
            await prompts.SetContextWindowAsync(7, 3);

            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var fakeReader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, fakeReader, settings, prompts);

            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(7, fakeReader.ReceivedBefore);
            Assert.Equal(3, fakeReader.ReceivedAfter);
        }

        // ---------------------------------------------------------------
        // Batch attribution
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
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, ctx) = MakeBatch(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "unknown"), (2, "Bob")));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Empty(result.Deferred);
            Assert.Equal(3, result.Outcomes.Count);
            Assert.Equal(batch[0], result.Outcomes[0].Item);
            Assert.Equal(AttributionStatus.Resolved, result.Outcomes[0].Outcome.Status);
            // Each index's segments are sliced from that index's own paragraph text.
            Assert.Equal("Text 0", Assert.Single(result.Outcomes[0].Outcome.Segments!).Text);
            Assert.Equal("Alice", result.Outcomes[0].Outcome.Segments![0].Speaker);
            Assert.Equal(AttributionStatus.Unknown, result.Outcomes[1].Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, result.Outcomes[2].Outcome.Status);
            Assert.Equal("Text 2", Assert.Single(result.Outcomes[2].Outcome.Segments!).Text);

            // One array-shaped, schema-constrained run for the whole batch.
            var request = Assert.Single(runner.Requests);
            Assert.Equal("3 paragraphs: P0", request.Label);
            Assert.Equal(CompletionShape.Array, request.Shape);
            Assert.Equal(SegmentBatchAttributionSchema.JsonSchema, request.JsonSchema);
        }

        [Fact]
        public async Task Batch_ExtraUnrequestedIndex_IsIgnored()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, ctx) = MakeBatch(2);
            // Models also answer for context paragraphs; indexes nobody asked for are dropped.
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "Bob"), (7, "Nobody")));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Single(runner.Requests);
            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_OfOne_DelegatesToSinglePath()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice", "calm"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync([TestItem], CancellationToken.None);

            var (item, outcome) = Assert.Single(result.Outcomes);
            Assert.Equal(TestItem, item);
            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal("Alice", Assert.Single(outcome.Segments!).Speaker);
            // Single path — the single-paragraph reader method was used, not the batch one.
            Assert.Null(reader.ReceivedBatchIds);
        }

        [Fact]
        public async Task Batch_UnparseableResponse_FallsBackToSinglePerItem()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, ctx) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes("not json at all")
                .Completes(Answer("Alice"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            // 1 batch run + 2 single fallbacks
            Assert.Equal(3, runner.Requests.Count);
            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_MissingIndex_FallsBackToSingleForTheWholeChunk()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            // Escalation's unit is the paragraph, so a batch answer that skips a requested index is
            // rejected whole — the chunk falls back to the single path, not just the missing item.
            var (batch, ctx) = MakeBatch(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (2, "Bob")))
                .Completes(Answer("Fallback"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            // 1 batch run + 3 single fallbacks.
            Assert.Equal(4, runner.Requests.Count);
            Assert.All(result.Outcomes, o =>
                Assert.Equal("Fallback", Assert.Single(o.Outcome.Segments!).Speaker));
        }

        [Fact]
        public async Task Batch_DeferredIds_ReturnedAsDeferredItems()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, _) = MakeBatch(3);
            // Context only includes the first two; the third was trimmed off the run.
            var ctx = new ParagraphBatchContext(
                [new BatchContextEntry("Text 0", [], 0), new BatchContextEntry("Text 1", [], 1)],
                [batch[0].ParagraphId, batch[1].ParagraphId],
                [batch[2].ParagraphId]);
            var runner = new FakeLlmCompletionRunner()
                .Completes(BatchAnswer((0, "Alice"), (1, "Bob")));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Equal(2, result.Outcomes.Count);
            var deferred = Assert.Single(result.Deferred);
            Assert.Equal(batch[2], deferred);
        }

        [Fact]
        public async Task Batch_IncludedRunOfOne_UsesSinglePathAndReturnsDeferred()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, _) = MakeBatch(2);
            var ctx = new ParagraphBatchContext(
                [new BatchContextEntry("Text 0", [], 0)],
                [batch[0].ParagraphId],
                [batch[1].ParagraphId]);
            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            var (item, outcome) = Assert.Single(result.Outcomes);
            Assert.Equal(batch[0], item);
            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal(batch[1], Assert.Single(result.Deferred));
        }

        [Fact]
        public async Task Batch_NoActiveConfig_AllItemsNoLlmConfigured()
        {
            var (batch, _) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner().Completes("unused");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Empty(runner.Requests);
            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.Equal(AttributionStatus.NoLlmConfigured, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_NullContext_FallsBackToSinglePerItem()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, _) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner()
                .Completes(Answer("Alice"));
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = null };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_ServiceUnavailableRun_AllItemsServiceUnavailable()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, ctx) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.ServiceUnavailable, "stalled");
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.Equal(AttributionStatus.ServiceUnavailable, o.Outcome.Status));
        }
    }
}
