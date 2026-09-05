using Read2Me.App.Characters;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Tests.Fakes;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services.Characters;

public class GeneratePromptsPhaseTests
{
    private static readonly ProjectFolderId Folder = new("test-book");

    [Fact]
    public async Task PlanAsync_MarksOnlyLinkedCharacterAsAlsoNarrating()
    {
        var watson = new Character { Id = Guid.NewGuid(), Name = "Dr. Watson" };
        var holmes = new Character { Id = Guid.NewGuid(), Name = "Sherlock Holmes" };
        var reader = new ReaderFake([watson, holmes])
        {
            Narrator = new NarratorIdentity(watson.Id, watson.Name, true),
        };
        var sut = new GeneratePromptsPhase();

        var result = await sut.PlanAsync(new PhaseDeps(reader, null!, null!, null!), Folder, CancellationToken.None);

        Assert.True(Assert.Single(result, item => item.CharacterId == watson.Id).AlsoNarrates);
        Assert.False(Assert.Single(result, item => item.CharacterId == holmes.Id).AlsoNarrates);
    }

    private sealed class ReaderFake(IReadOnlyList<Character> characters) : ProjectReaderFakeBase
    {
        public NarratorIdentity Narrator { get; init; } = NarratorIdentity.Unlinked;

        public override Task<Project?> GetProjectAsync(ProjectFolderId folderId) =>
            Task.FromResult<Project?>(new Project { BookTitle = "A Study in Scarlet", Author = "Arthur Conan Doyle" });

        public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) =>
            Task.FromResult(characters.ToList());

        public override Task<List<VoiceEntity>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId) =>
            Task.FromResult(new List<VoiceEntity>());

        public override Task<NarratorIdentity> GetNarratorAsync(
            ProjectFolderId folderId, CancellationToken ct = default) =>
            Task.FromResult(Narrator);
    }
}
