using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// The Voices step of the Angular pipeline stepper: how many characters exist, how many speak,
    /// and how many of the speakers already have a voice with audio.
    /// </summary>
    public class CastCountsTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public CastCountsTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private async Task AddVoiceAsync(Guid characterId, string? audioFileName)
        {
            await using var db = await OpenDbAsync();
            db.Voices.Add(new Data.Entities.Voice
            {
                Id = Guid.NewGuid(),
                CharacterId = characterId,
                Name = "v",
                AudioFileName = audioFileName,
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task EmptyProject_AllZero()
        {
            await new BookHierarchyBuilder(OpenDbAsync).BuildAsync();

            var counts = await _reader.GetCastCountsAsync(_folder);

            Assert.Equal(new CastCounts(0, 0, 0), counts);
        }

        [Fact]
        public async Task SeedNarratorRow_IsNotACharacter_ButNarrationMakesItASpeaker()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("v", v => v.AddChapter("c", c => c.AddParagraph("p", p => p.AddNarration("n")))).BuildAsync();

            var counts = await _reader.GetCastCountsAsync(_folder);

            Assert.Equal(0, counts.Characters);
            Assert.Equal(1, counts.CharactersWithLines);
            Assert.Equal(0, counts.ReadyVoices);
        }

        [Fact]
        public async Task CountsSpeakersAndOnlySpeakersWithAnAudioVoiceAsReady()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var bob = new Character { Id = Guid.NewGuid(), Name = "Bob" };
            var silent = new Character { Id = Guid.NewGuid(), Name = "Silent" };
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", alice)
                .WithCharacter("bob", bob)
                .WithCharacter("silent", silent);
            await b.AddVolume("v", v => v.AddChapter("c", c => c
                .AddParagraph("p1", p => p.AddCharacterLine("a1", "Hi", "alice").AddCharacterLine("a2", "Again", "alice"))
                .AddParagraph("p2", p => p.AddCharacterLine("b1", "Yo", "bob").AddRawItem("u", Data.Enums.ParagraphItemType.Speech, "?"))))
                .BuildAsync();
            await AddVoiceAsync(alice.Id, "alice.wav");
            await AddVoiceAsync(alice.Id, "alice2.wav");
            await AddVoiceAsync(bob.Id, null);
            await AddVoiceAsync(silent.Id, "silent.wav");

            var counts = await _reader.GetCastCountsAsync(_folder);

            Assert.Equal(new CastCounts(Characters: 3, CharactersWithLines: 2, ReadyVoices: 1), counts);
        }

        [Fact]
        public async Task LinkedNarrator_NarrationIsTheLinkedCharactersLines()
        {
            var watson = new Character { Id = Guid.NewGuid(), Name = "Watson" };
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("watson", watson)
                .WithNarratorLink(watson.Id);
            await b.AddVolume("v", v => v.AddChapter("c", c => c
                .AddParagraph("p1", p => p.AddNarration("n").AddCharacterLine("w", "Holmes!", "watson"))))
                .BuildAsync();
            await AddVoiceAsync(watson.Id, "watson.wav");

            var counts = await _reader.GetCastCountsAsync(_folder);

            Assert.Equal(new CastCounts(Characters: 1, CharactersWithLines: 1, ReadyVoices: 1), counts);
        }
    }
}
