using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// The single write path for the narrator link. Unlike its sibling handlers it throws
    /// rather than returning null on a bad target: the endpoint turns that into a 422, where
    /// <c>return null</c> would render rejection as <c>200 { "newEntityId": null }</c> to a
    /// machine caller (ADR-0004, spec §9).
    /// </summary>
    public class SetNarratorCharacterHandlerTests : ProjectDbTestBase
    {
        private readonly SetNarratorCharacterHandler _handler;
        private readonly ProjectFolderId _folder;

        public SetNarratorCharacterHandlerTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _handler = new SetNarratorCharacterHandler(session);
            _folder = new ProjectFolderId(FolderName);
        }

        private static readonly Guid WatsonId = Guid.NewGuid();
        private static readonly Guid HolmesId = Guid.NewGuid();

        private async Task SeedAsync(Guid? link = null)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("watson", new Character { Id = WatsonId, Name = "Dr. Watson" })
                .WithCharacter("holmes", new Character { Id = HolmesId, Name = "Holmes" });
            if (link.HasValue) b.WithNarratorLink(link.Value);
            await b.AddVolume("vol", v => v.AddChapter()).BuildAsync();
        }

        private async Task<NarratorIdentity> IdentityAsync()
        {
            await using var db = await OpenDbAsync();
            return await NarratorIdentity.LoadAsync(db);
        }

        private Task<Guid?> RunAsync(Guid? characterId) =>
            _handler.HandleAsync(new SetNarratorCharacterCommand(_folder, characterId), CancellationToken.None);

        [Fact]
        public async Task Set_LinksTheCharacter()
        {
            await SeedAsync();

            var result = await RunAsync(WatsonId);

            Assert.Null(result);
            var identity = await IdentityAsync();
            Assert.Equal(WatsonId, identity.CharacterId);
            Assert.Equal("Dr. Watson", identity.DisplayName);
            Assert.True(identity.IsLinked);
        }

        [Fact]
        public async Task Set_OverAnExistingLink_Changes()
        {
            await SeedAsync(link: WatsonId);

            await RunAsync(HolmesId);

            Assert.Equal(HolmesId, (await IdentityAsync()).CharacterId);
        }

        [Fact]
        public async Task Null_Unlinks()
        {
            await SeedAsync(link: WatsonId);

            await RunAsync(null);

            Assert.Equal(NarratorIdentity.Unlinked, await IdentityAsync());
        }

        [Fact]
        public async Task Null_OnAnAlreadyUnlinkedProject_IsAccepted()
        {
            await SeedAsync();

            await RunAsync(null);

            Assert.Equal(NarratorIdentity.Unlinked, await IdentityAsync());
        }

        /// <summary>
        /// Covers the spec's "foreign id" case too: each project owns its own SQLite file, so a
        /// character belonging to another project is simply an id this project's Characters table
        /// does not hold — the same lookup, the same rejection.
        /// </summary>
        [Fact]
        public async Task UnknownCharacterId_Throws_AndLeavesTheLinkAlone()
        {
            await SeedAsync(link: WatsonId);

            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Guid.NewGuid()));

            Assert.Equal(WatsonId, (await IdentityAsync()).CharacterId);
        }

        [Fact]
        public async Task SeedNarratorRow_Throws()
        {
            // Linking the narrator to itself is nonsense: it *is* the unlinked state.
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
