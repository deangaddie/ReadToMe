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
        public async Task ValidJsonResponse_ReturnsResolved_WithCharacterAndVoice()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Completes("""{ "character": "Alice", "voice_instructions": "calm" }""");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", result.Character);
            Assert.Equal("calm", result.VoiceInstructions);

            // Single path runs a schema-constrained object completion labelled with the preview.
            var request = Assert.Single(runner.Requests);
            Assert.Equal("Preview", request.Label);
            Assert.Equal(CompletionShape.Object, request.Shape);
            Assert.Equal(CharacterAttributionSchema.JsonSchema, request.JsonSchema);
        }

        [Fact]
        public async Task ResponseWithReasoningField_ParsesAndResolves()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner().Completes(
                """{ "reasoning": "the tag 'said Alice' follows the quote", "character": "Alice", "voice_instructions": "calm" }""");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", result.Character);
            Assert.Equal("calm", result.VoiceInstructions);
        }

        [Fact]
        public async Task LlmReturnsUnknown_ReturnsUnknownStatus()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Completes("""{ "character": "unknown", "voice_instructions": "" }""");
            var svc = NewService(runner, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
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
                .Completes("""{ "character": "Alice", "voice_instructions": "" }""");
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
                .Completes("""{ "character": "Alice", "voice_instructions": "" }""");
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
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "character": "Alice", "voice_instructions": "calm" },
                  { "index": 1, "character": "unknown" },
                  { "index": 2, "character": "Bob" } ]
                """);
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Empty(result.Deferred);
            Assert.Equal(3, result.Outcomes.Count);
            Assert.Equal(batch[0], result.Outcomes[0].Item);
            Assert.Equal(AttributionStatus.Resolved, result.Outcomes[0].Outcome.Status);
            Assert.Equal("Alice", result.Outcomes[0].Outcome.Character);
            Assert.Equal("calm", result.Outcomes[0].Outcome.VoiceInstructions);
            Assert.Equal(AttributionStatus.Unknown, result.Outcomes[1].Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, result.Outcomes[2].Outcome.Status);
            Assert.Equal("Bob", result.Outcomes[2].Outcome.Character);

            // One array-shaped, schema-constrained run for the whole batch.
            var request = Assert.Single(runner.Requests);
            Assert.Equal("3 paragraphs: P0", request.Label);
            Assert.Equal(CompletionShape.Array, request.Shape);
            Assert.Equal(CharacterBatchAttributionSchema.JsonSchema, request.JsonSchema);
        }

        [Fact]
        public async Task Batch_ResponseWithReasoningField_ParsesAndResolves()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, ctx) = MakeBatch(2);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "attribution tag after the quote", "character": "Alice", "voice_instructions": "calm" },
                  { "index": 1, "reasoning": "two-person alternation", "character": "Bob", "voice_instructions": "gruff" } ]
                """);
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Single(runner.Requests);
            Assert.Equal(AttributionStatus.Resolved, result.Outcomes[0].Outcome.Status);
            Assert.Equal("Alice", result.Outcomes[0].Outcome.Character);
            Assert.Equal(AttributionStatus.Resolved, result.Outcomes[1].Outcome.Status);
            Assert.Equal("Bob", result.Outcomes[1].Outcome.Character);
        }

        [Fact]
        public async Task Batch_OfOne_DelegatesToSinglePath()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var runner = new FakeLlmCompletionRunner()
                .Completes("""{ "character": "Alice", "voice_instructions": "calm" }""");
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync([TestItem], CancellationToken.None);

            var (item, outcome) = Assert.Single(result.Outcomes);
            Assert.Equal(TestItem, item);
            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal("Alice", outcome.Character);
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
                .Completes("""{ "character": "Alice", "voice_instructions": "" }""");
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            // 1 batch run + 2 single fallbacks
            Assert.Equal(3, runner.Requests.Count);
            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.Equal(AttributionStatus.Resolved, o.Outcome.Status));
        }

        [Fact]
        public async Task Batch_MissingIndex_FallsBackToSingleForThatItemOnly()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var (batch, ctx) = MakeBatch(3);
            var runner = new FakeLlmCompletionRunner()
                .Completes("""[ { "index": 0, "character": "Alice" }, { "index": 2, "character": "Bob" } ]""")
                .Completes("""{ "character": "Fallback", "voice_instructions": "" }""");
            var reader = new FakeProjectReader(DefaultContext(), DefaultProject()) { BatchContext = ctx };
            var svc = NewService(runner, reader, settings);

            var result = await svc.AttributeBatchAsync(batch, CancellationToken.None);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal("Alice", result.Outcomes[0].Outcome.Character);
            Assert.Equal("Fallback", result.Outcomes[1].Outcome.Character);
            Assert.Equal("Bob", result.Outcomes[2].Outcome.Character);
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
                .Completes("""[ { "index": 0, "character": "Alice" }, { "index": 1, "character": "Bob" } ]""");
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
                .Completes("""{ "character": "Alice", "voice_instructions": "" }""");
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
                .Completes("""{ "character": "Alice", "voice_instructions": "" }""");
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
