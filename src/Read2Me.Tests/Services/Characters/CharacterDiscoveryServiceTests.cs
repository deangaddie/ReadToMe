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
            FakeLlmCompletionRunner runner, LlmSettingsService settings)
        {
            var reader = new DiscoveryReader();
            return new(runner, settings, reader, new ChapterOutlineBuilder(reader), NewPrompts(),
                NullLogger<CharacterDiscoveryService>.Instance);
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
            var runner = new FakeLlmCompletionRunner().Completes(ValidJson);
            var outcome = await NewService(runner, NewSettings())
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.NoLlmConfigured, outcome.Status);
            Assert.Empty(outcome.Characters);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Discover_ValidResponse_ReturnsOkWithCharacters()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var outcome = await NewService(new FakeLlmCompletionRunner().Completes(ValidJson), settings)
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.Ok, outcome.Status);
            var c = Assert.Single(outcome.Characters);
            Assert.Equal("Bilbo", c.Name);
            Assert.Equal(["Mr. Baggins"], c.Aliases);
        }

        [Fact]
        public async Task Discover_Request_CarriesPromptSchemaAndObjectShape()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes(ValidJson);

            await NewService(runner, settings).DiscoverAsync(Folder, CancellationToken.None);

            var request = Assert.Single(runner.Requests);
            Assert.Equal("Discover characters", request.Label);
            Assert.Equal(CompletionShape.Object, request.Shape);
            Assert.Equal(CharacterDiscoverySchema.JsonSchema, request.JsonSchema);

            var prompt = request.Prompt;
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

            var outcome = await NewService(new FakeLlmCompletionRunner().Completes("not json at all"), settings)
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.Reason);
        }

        [Fact]
        public async Task Discover_RunFailed_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.Failed, "boom");

            var outcome = await NewService(runner, settings)
                .DiscoverAsync(Folder, CancellationToken.None);

            Assert.Equal(DiscoveryStatus.Failed, outcome.Status);
            Assert.Equal("boom", outcome.Reason);
        }

        [Fact]
        public async Task Discover_ServiceUnavailable_ReturnsServiceUnavailable()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.ServiceUnavailable, "down");

            var outcome = await NewService(runner, settings)
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
            var runner = new FakeLlmCompletionRunner().Throws(new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => NewService(runner, settings).DiscoverAsync(Folder, cts.Token));
        }
    }
}
