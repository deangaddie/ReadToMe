using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// The narrator link's command shape. What it writes is
    /// <c>SetNarratorCharacterMutation</c> — proved in
    /// <see cref="Tests.Services.Mutations.CharacterLifecycleMutationTests"/> — so what is left to
    /// hold here is the one way this command differs from every sibling: a refusal throws, and
    /// <c>CommandEndpoints</c> turns that into a 422. Answering null would render rejection to an
    /// agent as <c>200 { "newEntityId": null }</c>, indistinguishable from success (ADR-0004, spec §9).
    /// </summary>
    public class SetNarratorCharacterHandlerTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        public SetNarratorCharacterHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _folder = new ProjectFolderId(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private static readonly Guid WatsonId = Guid.NewGuid();

        private Task SeedAsync() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("watson", new Character { Id = WatsonId, Name = "Dr. Watson" })
                .AddVolume("vol", v => v.AddChapter()).BuildAsync();

        private async Task<Guid?> RunAsync(Guid? characterId)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBookCommandHandler>()
                .ExecuteAsync(new SetNarratorCharacterCommand(_folder, characterId));
        }

        private async Task<NarratorIdentity> IdentityAsync()
        {
            await using var db = await OpenDbAsync();
            return await NarratorIdentity.LoadAsync(db);
        }

        [Fact]
        public async Task Set_LinksTheCharacter_AndAnswersNull()
        {
            await SeedAsync();

            Assert.Null(await RunAsync(WatsonId));
            Assert.Equal(WatsonId, (await IdentityAsync()).CharacterId);
        }

        [Fact]
        public async Task Set_ToTheLinkAlreadyThere_AnswersNullRatherThanThrowing()
        {
            await SeedAsync();
            await RunAsync(WatsonId);

            Assert.Null(await RunAsync(WatsonId));
            Assert.Equal(WatsonId, (await IdentityAsync()).CharacterId);
        }

        [Fact]
        public async Task UnknownCharacterId_Throws_AndLeavesTheLinkAlone()
        {
            await SeedAsync();
            await RunAsync(WatsonId);

            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Guid.NewGuid()));

            Assert.Equal(WatsonId, (await IdentityAsync()).CharacterId);
        }

        [Fact]
        public async Task SeedNarratorRow_Throws()
        {
            await SeedAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RunAsync(ProjectDbContext.NarratorId));

            Assert.Contains("Narrator", ex.Message, StringComparison.Ordinal);
            Assert.False((await IdentityAsync()).IsLinked);
        }

        [Fact]
        public async Task NoProjectRow_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(null));

            Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
