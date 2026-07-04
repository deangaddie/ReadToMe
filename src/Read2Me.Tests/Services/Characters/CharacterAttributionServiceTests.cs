using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
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

        private CharacterAttributionService NewService(ILlmClient llm, IProjectReader reader,
            LlmSettingsService? settings = null, LlmPromptService? prompts = null,
            EventBroadcaster<LlmStreamEvent>? broadcaster = null,
            IAiServiceReporter? reporter = null) =>
            new(llm, settings ?? NewSettings(), prompts ?? NewPrompts(), reader,
                NullLogger<CharacterAttributionService>.Instance,
                broadcaster ?? new EventBroadcaster<LlmStreamEvent>(),
                reporter ?? new FakeAiServiceReporter());

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

        private sealed class FakeLlmClient : ILlmClient
        {
            public bool WasCalled { get; private set; }
            private readonly string _response;
            private readonly Exception? _throws;

            public FakeLlmClient(string response = "", Exception? throws = null)
            {
                _response = response;
                _throws = throws;
            }

            public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
                LlmServerConfig config, string prompt,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                WasCalled = true;
                if (_throws != null) throw _throws;
                yield return new LlmChatChunk(null, _response, false);
                yield return new LlmChatChunk(null, null, Done: true);
                await Task.CompletedTask;
            }

            public Task<IReadOnlyList<string>> GetModelsAsync(LlmServerConfig config, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

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
            public Task<List<VoiceRuleRow>> GetCharacterVoiceRulesAsync(ProjectFolderId folderId, Guid characterId)
                => Task.FromResult(new List<VoiceRuleRow>());
        }

        private static ParagraphContext DefaultContext() =>
            new(new ContextParagraph("Hello world", null), [], []);

        private static Project DefaultProject() =>
            new() { Id = Guid.NewGuid(), Title = "Book", BookTitle = "The Book", Author = "Author", Filename = "b.epub" };

        // ---------------------------------------------------------------
        // Tests
        // ---------------------------------------------------------------

        [Fact]
        public async Task NoActiveConfig_ReturnsNoLlmConfigured_WithoutCallingLlm()
        {
            var llm = new FakeLlmClient();
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()));

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.NoLlmConfigured, result.Status);
            Assert.False(llm.WasCalled);
        }

        [Fact]
        public async Task ValidJsonResponse_ReturnsResolved_WithCharacterAndVoice()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient("""{ "character": "Alice", "voice_instructions": "calm" }""");
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

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

            var llm = new FakeLlmClient("""{ "character": "unknown", "voice_instructions": "" }""");
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
        }

        [Fact]
        public async Task LlmThrows_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient(throws: new InvalidOperationException("connection refused"));
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Contains("connection refused", result.FailureReason);
        }

        [Fact]
        public async Task UnparseableResponse_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient("This is not JSON at all.");
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Failed, result.Status);
        }

        [Fact]
        public async Task BlankQueryText_ReturnsUnknown_WithoutCallingLlm()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient();
            var ctx = new ParagraphContext(new ContextParagraph("   ", null), [], []);
            var svc = NewService(llm, new FakeProjectReader(ctx, DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.False(llm.WasCalled);
        }

        [Fact]
        public async Task NullContext_ReturnsUnknown_WithoutCallingLlm()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient();
            var svc = NewService(llm, new FakeProjectReader(null, DefaultProject()), settings);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Unknown, result.Status);
            Assert.False(llm.WasCalled);
        }

        [Fact]
        public async Task ContextWindowDefaults_PassedToReader()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient("""{ "character": "Alice", "voice_instructions": "" }""");
            var fakeReader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(llm, fakeReader, settings);

            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(4, fakeReader.ReceivedBefore);
            Assert.Equal(2, fakeReader.ReceivedAfter);
        }

        [Fact]
        public async Task CustomContextWindow_PassedToReader()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var prompts = NewPrompts();
            await prompts.SetContextWindowAsync(7, 3);

            var llm = new FakeLlmClient("""{ "character": "Alice", "voice_instructions": "" }""");
            var fakeReader = new FakeProjectReader(DefaultContext(), DefaultProject());
            var svc = NewService(llm, fakeReader, settings, prompts);

            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(7, fakeReader.ReceivedBefore);
            Assert.Equal(3, fakeReader.ReceivedAfter);
        }

        // ---------------------------------------------------------------
        // Watchdog reporting
        // ---------------------------------------------------------------

        [Fact]
        public async Task ManagedServiceThrows_ReportsFailure_ReturnsServiceUnavailable()
        {
            var settings = NewSettings();
            var config = await RegisterActiveConfigAsync(settings);

            var reporter = new FakeAiServiceReporter { Managed = true };
            var llm = new FakeLlmClient(throws: new AiServiceStalledException(config.BaseUrl, TimeSpan.FromSeconds(120)));
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings, reporter: reporter);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.ServiceUnavailable, result.Status);
            var (baseUrl, _) = Assert.Single(reporter.Failures);
            Assert.Equal(config.BaseUrl, baseUrl);
        }

        [Fact]
        public async Task RemoteServiceThrows_NotReported_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var reporter = new FakeAiServiceReporter { Managed = false }; // registry miss
            var llm = new FakeLlmClient(throws: new InvalidOperationException("connection refused"));
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings, reporter: reporter);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Failed, result.Status);
            Assert.Contains("connection refused", result.FailureReason);
        }

        [Fact]
        public async Task ManagedServiceSucceeds_ReportsSuccess()
        {
            var settings = NewSettings();
            var config = await RegisterActiveConfigAsync(settings);

            var reporter = new FakeAiServiceReporter { Managed = true };
            var llm = new FakeLlmClient("""{ "character": "Alice", "voice_instructions": "" }""");
            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()), settings, reporter: reporter);

            var result = await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Contains(config.BaseUrl, reporter.Successes);
        }

        // ---------------------------------------------------------------
        // Broadcaster tests
        // ---------------------------------------------------------------

        [Fact]
        public async Task Broadcaster_SuccessfulAttribution_PublishesExpectedSequence()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClientWithThinking(
                thinking: "Let me think...",
                content: """{ "character": "Alice", "voice_instructions": "calm" }""");
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += e => events.Add(e);

            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()),
                settings, broadcaster: broadcaster);
            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.IsType<RequestStarted>(events[0]);
            Assert.Contains(events, e => e is ThinkingDelta { Text: "Let me think..." });
            Assert.Contains(events, e => e is ContentDelta);
            Assert.IsType<StreamCompleted>(events[^1]);
            var completed = (StreamCompleted)events[^1];
            Assert.True(completed.TokensOut > 0);
        }

        [Fact]
        public async Task Broadcaster_ParseFailure_PublishesStreamFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient("not json");
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += e => events.Add(e);

            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()),
                settings, broadcaster: broadcaster);
            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Contains(events, e => e is StreamCompleted);
            Assert.Contains(events, e => e is StreamFailed);
        }

        [Fact]
        public async Task Broadcaster_LlmException_PublishesStreamFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var llm = new FakeLlmClient(throws: new InvalidOperationException("network down"));
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += e => events.Add(e);

            var svc = NewService(llm, new FakeProjectReader(DefaultContext(), DefaultProject()),
                settings, broadcaster: broadcaster);
            await svc.AttributeAsync(TestItem, CancellationToken.None);

            Assert.Contains(events, e => e is StreamFailed sf && sf.Reason.Contains("network down"));
        }

        private sealed class FakeLlmClientWithThinking : ILlmClient
        {
            private readonly string _thinking;
            private readonly string _content;

            public FakeLlmClientWithThinking(string thinking, string content)
            {
                _thinking = thinking;
                _content = content;
            }

            public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
                LlmServerConfig config, string prompt,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new LlmChatChunk(_thinking, null, false);
                yield return new LlmChatChunk(null, _content, false);
                yield return new LlmChatChunk(null, null, Done: true);
                await Task.CompletedTask;
            }

            public Task<IReadOnlyList<string>> GetModelsAsync(LlmServerConfig config, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }
}
