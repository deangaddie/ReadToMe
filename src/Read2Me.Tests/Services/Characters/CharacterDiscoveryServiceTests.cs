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
    public class CharacterDiscoveryServiceTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private LlmSettingsService NewSettings() =>
            new(Factory, NullLogger<LlmSettingsService>.Instance);

        private LlmPromptService NewPrompts() =>
            new(Factory, NullLogger<LlmPromptService>.Instance);

        private CharacterDiscoveryService NewService(
            FakeLlmClient llm, LlmSettingsService settings, FakeAiServiceReporter? reporter = null)
        {
            var reader = new DiscoveryReader();
            return new(llm, settings, reader, new ChapterOutlineBuilder(reader), NewPrompts(),
                NullLogger<CharacterDiscoveryService>.Instance,
                new EventBroadcaster<LlmStreamEvent>(),
                reporter ?? new FakeAiServiceReporter());
        }

        /// <summary>Reader that returns a known book: title, author, one chapter, one known character.</summary>
        private sealed class DiscoveryReader : ProjectReaderFakeBase
        {
            private static readonly Guid VolumeId = Guid.NewGuid();
            private static readonly Guid PartId = Guid.NewGuid();

            public override Task<Project?> GetProjectAsync(ProjectFolderId folderId) =>
                Task.FromResult<Project?>(new Project { BookTitle = "The Hobbit", Author = "J.R.R. Tolkien" });

            public override Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId) =>
                Task.FromResult(new List<Volume> { new() { Id = VolumeId, Title = "Vol 1" } });

            public override Task<int> GetTotalPartCountAsync(ProjectFolderId folderId) => Task.FromResult(1);
            public override Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId) => Task.FromResult(1);

            public override Task<HierarchyChildren> GetChildrenAsync(ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId) =>
                Task.FromResult(parentLevel switch
                {
                    BookNodeLevel.Volume => new HierarchyChildren([new Part { Id = PartId }], null, null),
                    BookNodeLevel.Part => new HierarchyChildren(null, [new Chapter { Id = Guid.NewGuid(), Title = "An Unexpected Party" }], null),
                    _ => new HierarchyChildren(null, null, null),
                });

            public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) =>
                Task.FromResult(new List<Character>
                {
                    new() { Id = Guid.NewGuid(), Name = "Gandalf", Aliases = [new CharacterAlias { Name = "the wizard" }] },
                });
        }

        private const string ValidJson =
            """{ "reasoning": "well-known cast", "characters": [ { "name": "Bilbo", "aliases": ["Mr. Baggins"] } ] }""";

        private static async Task RegisterActiveConfigAsync(LlmSettingsService svc)
        {
            var config = new LlmServerConfig { Name = "Test", BaseUrl = "http://localhost:8080", Model = "test" };
            config = await svc.CreateConfigAsync(config);
            await svc.SetActiveConfigAsync(config.Id);
        }

        [Fact]
        public async Task Discover_NoConfig_ReturnsNoLlmConfigured()
        {
            var outcome = await NewService(new FakeLlmClient(ValidJson), NewSettings())
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.NoLlmConfigured, outcome.Status);
            Assert.Empty(outcome.Characters);
        }

        [Fact]
        public async Task Discover_ValidResponse_ReturnsOkWithCharacters()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var outcome = await NewService(new FakeLlmClient(ValidJson), settings)
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.Ok, outcome.Status);
            var c = Assert.Single(outcome.Characters);
            Assert.Equal("Bilbo", c.Name);
            Assert.Equal(["Mr. Baggins"], c.Aliases);
        }

        [Fact]
        public async Task Discover_Prompt_ContainsTitleAuthorOutlineAndKnownCharacters()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient(ValidJson);

            await NewService(llm, settings).DiscoverAsync(Folder, CancellationToken.None);

            var prompt = llm.Prompts[0];
            Assert.Contains("The Hobbit", prompt);
            Assert.Contains("J.R.R. Tolkien", prompt);
            Assert.Contains("An Unexpected Party", prompt);   // chapter outline
            Assert.Contains("Gandalf", prompt);               // known character
            Assert.Contains("the wizard", prompt);            // known alias
        }

        [Fact]
        public async Task Discover_GarbageResponse_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var outcome = await NewService(new FakeLlmClient("not json at all"), settings)
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.Reason);
        }

        [Fact]
        public async Task Discover_LlmThrows_Unmanaged_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient(ValidJson) { Throws = new HttpRequestException("boom") };

            var outcome = await NewService(llm, settings, new FakeAiServiceReporter { Managed = false })
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.Failed, outcome.Status);
            Assert.Equal("boom", outcome.Reason);
        }

        [Fact]
        public async Task Discover_LlmThrows_Managed_ReturnsServiceUnavailable()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient(ValidJson) { Throws = new HttpRequestException("down") };

            var outcome = await NewService(llm, settings, new FakeAiServiceReporter { Managed = true })
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.ServiceUnavailable, outcome.Status);
        }

        [Fact]
        public async Task Discover_Cancellation_Propagates()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var llm = new FakeLlmClient(ValidJson) { Throws = new OperationCanceledException() };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => NewService(llm, settings).DiscoverAsync(Folder, cts.Token));
        }
    }
}
