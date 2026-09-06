using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Read2Me.App.Characters;
using Read2Me.App.Services;
using Read2Me.Core.Audio;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The voice batch driven end to end through <see cref="VoiceBatchRunner"/>.
    /// <para>
    /// Its reads are faked — the roster and the LLM's plan are what each case is arranged from — but
    /// its <em>writes</em> go to a real project database through <c>BookMutations</c>, because that is
    /// what the batch is now a producer of (ADR 0007). So "the sweep created these voices" is asserted
    /// by reading them back rather than by watching commands go past.
    /// </para>
    /// </summary>
    public class VoiceBatchRunnerIntegrationTests : ProjectDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private ServiceProvider? _root;

        public override async ValueTask DisposeAsync()
        {
            if (_root is not null) await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static Character MakeCharacter(string name, bool isNarrator = false) =>
            new() { Id = Guid.NewGuid(), Name = name, IsNarrator = isNarrator };

        private static Character MakeSeedNarrator() => new()
        {
            Id = ProjectDbContext.NarratorId,
            Name = ProjectDbContext.NarratorName,
            IsNarrator = true,
        };

        private static VoiceEntity MakeGeneratedVoice(Guid characterId, string? designPrompt = null) =>
            new() { Id = Guid.NewGuid(), CharacterId = characterId, Name = "Default", Source = VoiceSource.Generated, DesignPrompt = designPrompt };

        private static VoiceEntity MakeUploadedVoice(Guid characterId) =>
            new() { Id = Guid.NewGuid(), CharacterId = characterId, Name = "Reference", Source = VoiceSource.Uploaded };

        private static async Task WaitForIdleAsync(VoiceBatchRunner sut, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (sut.IsRunning && DateTime.UtcNow < deadline)
                await Task.Delay(20);
        }

        private Harness BuildHarness(
            IReadOnlyList<Character>? characters = null,
            Dictionary<Guid, List<VoiceEntity>>? voicesByCharacter = null,
            string cannedPrompt = "a warm, resonant voice",
            IReadOnlyList<VoicePlanVoice>? cannedPlan = null,
            bool orchestratorThrows = false,
            int orchestratorDelayMs = 0,
            bool audioGenerationFails = false,
            bool audioGenerationThrows = false,
            string cannedAudioFileName = "voices/voice.wav",
            string cannedTranscript = "sample text",
            NarratorIdentity? narrator = null)
        {
            var chars = characters ?? Array.Empty<Character>();
            var voicesMap = voicesByCharacter ?? new Dictionary<Guid, List<VoiceEntity>>();

            SeedProject(chars, voicesMap);

            var fakeReader = new FakeProjectReader2(
                chars, voicesMap, "Test Book", "Test Author", narrator ?? NarratorIdentity.Unlinked);

            var fakeOrchestrator = new FakeVoiceOrchestrator(
                cannedPrompt, orchestratorThrows, orchestratorDelayMs,
                audioGenerationFails, audioGenerationThrows,
                cannedAudioFileName, cannedTranscript, cannedPlan);

            return BuildRunner(fakeReader, fakeOrchestrator);
        }

        /// <summary>
        /// The runner over a real write side: the mutation wiring and its project database, with the
        /// roster reads and the LLM standing in as fakes.
        /// </summary>
        private Harness BuildRunner(IProjectReader reader, VoiceOrchestrator orchestrator)
        {
            var events = new List<VoiceBatchEvent>();

            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddSingleton(reader);
            services.AddSingleton<VoiceOrchestrator>(orchestrator);
            _root = services.BuildServiceProvider();

            var broadcaster = new EventBroadcaster<VoiceBatchEvent>();
            broadcaster.Event += e => { lock (events) events.Add(e); };

            var sut = new VoiceBatchRunner(
                _root.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<VoiceBatchRunner>.Instance,
                broadcaster,
                new EventBroadcaster<LlmStreamEvent>());

            return new Harness(sut, events);
        }

        /// <summary>
        /// Puts the arranged roster and voices into the project database, so the writes the sweep
        /// makes land against the same Book the fake reader describes.
        /// </summary>
        private void SeedProject(
            IReadOnlyCollection<Character> characters, Dictionary<Guid, List<VoiceEntity>> voicesByCharacter)
        {
            using var db = OpenDbAsync().GetAwaiter().GetResult();
            db.Projects.Add(new Project
            {
                Title = "Test Book", BookTitle = "Test Book", Author = "Test Author",
                Filename = "t.txt", Type = BookFileType.Text,
            });

            foreach (var character in characters)
            {
                // The seed Narrator row is already there — it is created with the database, not by
                // anyone arranging a test (ADR-0004).
                if (!db.Characters.Any(c => c.Id == character.Id))
                    db.Characters.Add(new Character
                    {
                        Id = character.Id, Name = character.Name, IsNarrator = character.IsNarrator,
                    });

                if (!voicesByCharacter.TryGetValue(character.Id, out var voices)) continue;

                foreach (var voice in voices)
                    db.Voices.Add(new VoiceEntity
                    {
                        Id = voice.Id, CharacterId = character.Id, Name = voice.Name,
                        Source = voice.Source, DesignPrompt = voice.DesignPrompt,
                        AudioFileName = voice.AudioFileName, CreatedUtc = DateTime.UtcNow,
                    });
            }

            db.SaveChanges();
        }

        /// <summary>The voices the sweep left behind for one character, oldest first.</summary>
        private async Task<List<VoiceEntity>> ReadVoicesAsync(Guid characterId)
        {
            await using var db = await OpenDbAsync();
            return await db.Voices
                .Where(v => v.CharacterId == characterId)
                .OrderBy(v => v.CreatedUtc)
                .ToListAsync();
        }

        /// <summary>Every voice in the Book, for the cases that assert nothing was created at all.</summary>
        private async Task<List<VoiceEntity>> ReadAllVoicesAsync()
        {
            await using var db = await OpenDbAsync();
            return await db.Voices.OrderBy(v => v.CreatedUtc).ToListAsync();
        }

        // ── Tests ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task GeneratePrompts_CharacterWithNoVoice_CreatesAllPlannedVoices()
        {
            var character = MakeCharacter("Alice");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity>()
                },
                cannedPlan:
                [
                    new VoicePlanVoice("Young Alice", "Part 1, Chapter 1 to Part 1, Chapter 7", "a girl's voice"),
                    new VoicePlanVoice("Adult Alice", "Part 2 onwards", "a grown woman's voice"),
                ]);

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            var created = await ReadVoicesAsync(character.Id);

            Assert.Equal(2, created.Count);
            Assert.All(created, v => Assert.Equal(VoiceSource.Generated, v.Source));

            // Name, description and prompt land together — one commit per planned voice — so each
            // planned voice is checked as the whole row it became.
            var young = created.Single(v => v.Name == "Young Alice");
            Assert.Equal("Part 1, Chapter 1 to Part 1, Chapter 7", young.Description);
            Assert.Equal("a girl's voice", young.DesignPrompt);

            var adult = created.Single(v => v.Name == "Adult Alice");
            Assert.Equal("Part 2 onwards", adult.Description);
            Assert.Equal("a grown woman's voice", adult.DesignPrompt);
        }

        [Fact]
        public async Task GeneratePrompts_NarratorWithNoVoice_CreatesDefaultVoice()
        {
            var narrator = MakeSeedNarrator();
            var h = BuildHarness(
                characters: new[] { narrator },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [narrator.Id] = new List<VoiceEntity>()
                });

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Single(await ReadVoicesAsync(narrator.Id));
        }

        [Fact]
        public async Task RegenerateAllPrompts_UnlinkedSeedNarrator_IsReplanned()
        {
            var narrator = MakeSeedNarrator();
            var existingVoice = MakeGeneratedVoice(narrator.Id, "existing narrator prompt");
            var voices = new Dictionary<Guid, List<VoiceEntity>>
            {
                [narrator.Id] = [existingVoice],
            };
            var h = BuildHarness(characters: [narrator], voicesByCharacter: voices);

            h.Sut.StartGeneratePrompts(Folder, regenerateAll: true);
            await WaitForIdleAsync(h.Sut);

            var replanned = await ReadVoicesAsync(narrator.Id);
            Assert.DoesNotContain(replanned, v => v.Id == existingVoice.Id);
            Assert.NotEmpty(replanned);
        }

        [Fact]
        public async Task GeneratePrompts_LinkedSeedNarratorWithoutVoices_IsNotPlanned()
        {
            var narrator = MakeSeedNarrator();
            var watson = MakeCharacter("Dr. Watson");
            var h = BuildHarness(
                characters: [narrator, watson],
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [narrator.Id] = [],
                    [watson.Id] = [],
                },
                narrator: new NarratorIdentity(watson.Id, watson.Name, true));

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Empty(await ReadVoicesAsync(narrator.Id));
            Assert.NotEmpty(await ReadVoicesAsync(watson.Id));
        }

        [Fact]
        public async Task RegenerateAllPrompts_LinkedSeedNarrator_PreservesItsExistingVoices()
        {
            var narrator = MakeSeedNarrator();
            var watson = MakeCharacter("Dr. Watson");
            var narratorVoice = MakeGeneratedVoice(narrator.Id, "existing narrator prompt");
            var voices = new Dictionary<Guid, List<VoiceEntity>>
            {
                [narrator.Id] = [narratorVoice],
                [watson.Id] = [],
            };
            var h = BuildHarness(
                characters: [narrator, watson],
                voicesByCharacter: voices,
                narrator: new NarratorIdentity(watson.Id, watson.Name, true));

            h.Sut.StartGeneratePrompts(Folder, regenerateAll: true);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(await ReadVoicesAsync(narrator.Id), v => v.Id == narratorVoice.Id);
        }

        [Fact]
        public async Task GeneratePrompts_CharacterAlreadyHasVoice_Skipped()
        {
            // Characters with any existing voice are left alone — the plan sweep only
            // fills in characters that have no voices at all.
            var character = MakeCharacter("Bob");
            var voice = MakeGeneratedVoice(character.Id, designPrompt: null);
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity> { voice }
                });

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Equal([voice.Id], (await ReadAllVoicesAsync()).Select(v => v.Id));
        }

        [Fact]
        public async Task GeneratePrompts_UploadedVoice_NoPromptWritten()
        {
            var character = MakeCharacter("Carol");
            var uploaded = MakeUploadedVoice(character.Id);
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity> { uploaded }
                });

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Null((await ReadVoicesAsync(character.Id)).Single(v => v.Id == uploaded.Id).DesignPrompt);
        }

        [Fact]
        public async Task GeneratePrompts_PromptVoiceAlreadyHasPrompt_IsLeftAlone()
        {
            var character = MakeCharacter("Dave");
            var voice = MakeGeneratedVoice(character.Id, designPrompt: "existing prompt");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity> { voice }
                });

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Equal("existing prompt",
                (await ReadVoicesAsync(character.Id)).Single(v => v.Id == voice.Id).DesignPrompt);
        }

        [Fact]
        public async Task GeneratePrompts_OrchestratorThrowsForOneCharacter_SweepContinuesOthersProcessed()
        {
            var c1 = MakeCharacter("Eve");
            var c2 = MakeCharacter("Frank");

            // first orchestrator call throws, second succeeds
            var fakeOrchestrator = new SequencedFakeVoiceOrchestrator(
                new[] { true, false }, "good prompt");

            var voices = new Dictionary<Guid, List<VoiceEntity>>
            {
                [c1.Id] = new List<VoiceEntity>(),
                [c2.Id] = new List<VoiceEntity>(),
            };
            SeedProject([c1, c2], voices);

            var h = BuildRunner(new FakeProjectReader2([c1, c2], voices, "Book", "Author"), fakeOrchestrator);

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            // c2 should still get its voices even though c1 failed
            Assert.NotEmpty(await ReadVoicesAsync(c2.Id));
            Assert.Empty(await ReadVoicesAsync(c1.Id));
            Assert.Equal(1, h.Sut.Failed);
        }

        [Fact]
        public async Task GeneratePrompts_Idempotent_NothingWrittenWhenAllVoicesAlreadyPrompted()
        {
            var character = MakeCharacter("Grace");
            var voice = MakeGeneratedVoice(character.Id, designPrompt: "already set");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity> { voice }
                });

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            // Nothing created, and the prompt that was already there is untouched.
            var after = await ReadAllVoicesAsync();
            Assert.Equal([voice.Id], after.Select(v => v.Id));
            Assert.Equal("already set", after[0].DesignPrompt);
        }

        [Fact]
        public async Task StartGeneratePrompts_WhileRunning_ReturnsFalse()
        {
            var character = MakeCharacter("Hank");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity>()
                },
                orchestratorDelayMs: 3000);

            var r1 = h.Sut.StartGeneratePrompts(Folder);
            var r2 = h.Sut.StartGeneratePrompts(Folder);

            Assert.True(r1);
            Assert.False(r2);

            h.Sut.Cancel();
            await WaitForIdleAsync(h.Sut);
        }

        [Fact]
        public async Task GeneratePrompts_Success_CreatesVoicesAndPublishesBatchCompleted()
        {
            var character = MakeCharacter("Iris");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [character.Id] = new List<VoiceEntity>()
                },
                cannedPrompt: "silky smooth");

            h.Sut.StartGeneratePrompts(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(await ReadVoicesAsync(character.Id), v => v.DesignPrompt == "silky smooth");
            Assert.Contains(h.Events, e => e is BatchCompleted bc && bc.Processed == 1 && bc.Failed == 0);
        }

        [Fact]
        public async Task Cancel_StopsSweep_NoFurtherVoicesProcessedAfterCancel()
        {
            // Many characters with slow orchestrator — cancel should stop mid-sweep
            var characters = Enumerable.Range(0, 10)
                .Select(i => MakeCharacter($"Char{i}"))
                .ToArray();
            var voicesMap = characters.ToDictionary(
                c => c.Id,
                c => new List<VoiceEntity>());

            var h = BuildHarness(
                characters: characters,
                voicesByCharacter: voicesMap,
                orchestratorDelayMs: 500);

            h.Sut.StartGeneratePrompts(Folder);
            await Task.Delay(300); // let one or two process
            h.Sut.Cancel();
            await WaitForIdleAsync(h.Sut, timeoutMs: 8000);

            Assert.False(h.Sut.IsRunning);
            // Not all 10 processed
            Assert.True(h.Sut.Processed < 10, $"Expected < 10 processed but got {h.Sut.Processed}");
        }

        // ── Audio sweep tests ─────────────────────────────────────────────────

        private static VoiceEntity MakeGeneratedVoiceWithPrompt(Guid characterId, string? audioFileName = null) =>
            new() { Id = Guid.NewGuid(), CharacterId = characterId, Name = "Default", Source = VoiceSource.Generated, DesignPrompt = "a warm voice", AudioFileName = audioFileName };

        [Fact]
        public async Task GenerateAudio_PromptVoiceWithPromptAndNoAudio_InvokesGenerateVoiceAudio()
        {
            var character = MakeCharacter("Alice");
            var voice = MakeGeneratedVoiceWithPrompt(character.Id);
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { voice } },
                cannedAudioFileName: "voices/alice.wav",
                cannedTranscript: "hello world");

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == voice.Id && vu.AudioFileName == "voices/alice.wav" && vu.Transcript == "hello world");
        }

        [Fact]
        public async Task GenerateAudio_UnlinkedSeedNarrator_IsPlanned()
        {
            var narrator = MakeSeedNarrator();
            var voice = MakeGeneratedVoiceWithPrompt(narrator.Id);
            var h = BuildHarness(
                characters: [narrator],
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [narrator.Id] = [voice],
                });

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(h.Events,
                e => e is VoiceUpdated update && update.VoiceId == voice.Id);
        }

        [Fact]
        public async Task GenerateAudio_LinkedSeedNarrator_IsNotPlanned()
        {
            var narrator = MakeSeedNarrator();
            var watson = MakeCharacter("Dr. Watson");
            var narratorVoice = MakeGeneratedVoiceWithPrompt(narrator.Id);
            var watsonVoice = MakeGeneratedVoiceWithPrompt(watson.Id);
            var h = BuildHarness(
                characters: [narrator, watson],
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>>
                {
                    [narrator.Id] = [narratorVoice],
                    [watson.Id] = [watsonVoice],
                },
                narrator: new NarratorIdentity(watson.Id, watson.Name, true));

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.DoesNotContain(h.Events,
                e => e is VoiceUpdated update && update.VoiceId == narratorVoice.Id);
            Assert.Contains(h.Events,
                e => e is VoiceUpdated update && update.VoiceId == watsonVoice.Id);
        }

        [Fact]
        public async Task GenerateAudio_UploadedVoice_NotInvoked()
        {
            var character = MakeCharacter("Bob");
            var uploaded = MakeUploadedVoice(character.Id);
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { uploaded } });

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            // No VoiceUpdated with audio for an uploaded voice
            Assert.DoesNotContain(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == uploaded.Id && vu.AudioFileName != null);
        }

        [Fact]
        public async Task GenerateAudio_PromptVoiceWithBlankPrompt_NotInvoked()
        {
            var character = MakeCharacter("Carol");
            var voice = MakeGeneratedVoice(character.Id, designPrompt: null); // blank prompt
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { voice } });

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.DoesNotContain(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == voice.Id && vu.AudioFileName != null);
        }

        [Fact]
        public async Task GenerateAudio_VoiceAlreadyHasAudio_NotInvoked()
        {
            var character = MakeCharacter("Dave");
            var voice = MakeGeneratedVoiceWithPrompt(character.Id, audioFileName: "voices/existing.wav");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { voice } });

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.DoesNotContain(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == voice.Id && vu.AudioFileName != null);
        }

        [Fact]
        public async Task GenerateAudio_OneGenerationFails_CountedAndSweepContinues()
        {
            var c1 = MakeCharacter("Eve");
            var c2 = MakeCharacter("Frank");
            var v1 = MakeGeneratedVoiceWithPrompt(c1.Id);
            var v2 = MakeGeneratedVoiceWithPrompt(c2.Id);

            var fakeOrchestrator = new SequencedFakeAudioOrchestrator(
                shouldFail: new[] { true, false },
                cannedAudioFileName: "voices/frank.wav",
                cannedTranscript: "hello");

            var h = BuildSequencedAudioRunner(c1, c2, v1, v2, fakeOrchestrator);

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == v2.Id && vu.AudioFileName == "voices/frank.wav");
            Assert.Equal(1, h.Sut.Failed);
        }

        /// <summary>The two-character audio-sweep arrangement both sequenced-failure cases share.</summary>
        private Harness BuildSequencedAudioRunner(
            Character c1, Character c2, VoiceEntity v1, VoiceEntity v2, VoiceOrchestrator orchestrator)
        {
            var voices = new Dictionary<Guid, List<VoiceEntity>>
            {
                [c1.Id] = [v1],
                [c2.Id] = [v2],
            };
            SeedProject([c1, c2], voices);
            return BuildRunner(new FakeProjectReader2([c1, c2], voices, "Book", "Author"), orchestrator);
        }

        [Fact]
        public async Task GenerateAudio_OneGenerationThrows_CountedAndSweepContinues()
        {
            var c1 = MakeCharacter("Grace");
            var c2 = MakeCharacter("Hank");
            var v1 = MakeGeneratedVoiceWithPrompt(c1.Id);
            var v2 = MakeGeneratedVoiceWithPrompt(c2.Id);

            var fakeOrchestrator = new SequencedFakeAudioOrchestrator(
                shouldFail: new[] { false, false },
                shouldThrow: new[] { true, false },
                cannedAudioFileName: "voices/hank.wav",
                cannedTranscript: "hello");

            var h = BuildSequencedAudioRunner(c1, c2, v1, v2, fakeOrchestrator);

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == v2.Id && vu.AudioFileName == "voices/hank.wav");
            Assert.Equal(1, h.Sut.Failed);
        }

        [Fact]
        public async Task GenerateAudio_AllVoicesAlreadyHaveAudio_NoGenerationCalls()
        {
            var character = MakeCharacter("Iris");
            var voice = MakeGeneratedVoiceWithPrompt(character.Id, audioFileName: "voices/iris.wav");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { voice } });

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.DoesNotContain(h.Events, e => e is VoiceUpdated vu && vu.AudioFileName != null);
            Assert.Contains(h.Events, e => e is BatchCompleted bc && bc.Processed == 0 && bc.Failed == 0);
        }

        [Fact]
        public async Task StartGenerateAudio_WhileGeneratePromptsRunning_ReturnsFalse()
        {
            var character = MakeCharacter("Jack");
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity>() },
                orchestratorDelayMs: 3000);

            var r1 = h.Sut.StartGeneratePrompts(Folder);
            var r2 = h.Sut.StartGenerateAudio(Folder);

            Assert.True(r1);
            Assert.False(r2);

            h.Sut.Cancel();
            await WaitForIdleAsync(h.Sut);
        }

        [Fact]
        public async Task StartGeneratePrompts_WhileGenerateAudioRunning_ReturnsFalse()
        {
            var character = MakeCharacter("Kate");
            var voice = MakeGeneratedVoiceWithPrompt(character.Id);
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { voice } },
                orchestratorDelayMs: 3000);

            var r1 = h.Sut.StartGenerateAudio(Folder);
            var r2 = h.Sut.StartGeneratePrompts(Folder);

            Assert.True(r1);
            Assert.False(r2);

            h.Sut.Cancel();
            await WaitForIdleAsync(h.Sut);
        }

        [Fact]
        public async Task GenerateAudio_Success_PublishesVoiceUpdatedWithAudioAndBatchCompleted()
        {
            var character = MakeCharacter("Leo");
            var voice = MakeGeneratedVoiceWithPrompt(character.Id);
            var h = BuildHarness(
                characters: new[] { character },
                voicesByCharacter: new Dictionary<Guid, List<VoiceEntity>> { [character.Id] = new List<VoiceEntity> { voice } },
                cannedAudioFileName: "voices/leo.wav",
                cannedTranscript: "my transcript");

            h.Sut.StartGenerateAudio(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Contains(h.Events, e => e is VoiceUpdated vu && vu.VoiceId == voice.Id && vu.AudioFileName == "voices/leo.wav" && vu.Transcript == "my transcript");
            Assert.Contains(h.Events, e => e is BatchCompleted bc && bc.Processed == 1 && bc.Failed == 0);
        }

        // ── Fakes ──────────────────────────────────────────────────────────────

        private sealed record Harness(VoiceBatchRunner Sut, List<VoiceBatchEvent> Events);

        private sealed class FakeProjectReader2 : ProjectReaderFakeBase
        {
            private readonly IReadOnlyList<Character> _characters;
            private readonly Dictionary<Guid, List<VoiceEntity>> _voicesByCharacter;
            private readonly string _bookTitle;
            private readonly string _author;
            private readonly NarratorIdentity _narrator;

            public FakeProjectReader2(
                IEnumerable<Character> characters,
                Dictionary<Guid, List<VoiceEntity>> voicesByCharacter,
                string bookTitle,
                string author,
                NarratorIdentity? narrator = null)
            {
                _characters = characters.ToList();
                _voicesByCharacter = voicesByCharacter;
                _bookTitle = bookTitle;
                _author = author;
                _narrator = narrator ?? NarratorIdentity.Unlinked;
            }

            public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) =>
                Task.FromResult(_characters.ToList());

            public override Task<List<VoiceEntity>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId)
            {
                _voicesByCharacter.TryGetValue(characterId, out var voices);
                return Task.FromResult(voices?.ToList() ?? []);
            }

            public override Task<Project?> GetProjectAsync(ProjectFolderId folderId) =>
                Task.FromResult<Project?>(new Project { BookTitle = _bookTitle, Author = _author });

            public override Task<NarratorIdentity> GetNarratorAsync(
                ProjectFolderId folderId, CancellationToken ct = default) =>
                Task.FromResult(_narrator);
        }

        private sealed class FakeVoiceOrchestrator : VoiceOrchestrator
        {
            private readonly string _cannedPrompt;
            private readonly bool _throws;
            private readonly int _delayMs;
            private readonly bool _audioFails;
            private readonly bool _audioThrows;
            private readonly string _cannedAudioFileName;
            private readonly string _cannedTranscript;
            private readonly IReadOnlyList<VoicePlanVoice>? _cannedPlan;

            public FakeVoiceOrchestrator(
                string cannedPrompt, bool throws = false, int delayMs = 0,
                bool audioFails = false, bool audioThrows = false,
                string cannedAudioFileName = "voices/voice.wav", string cannedTranscript = "sample text",
                IReadOnlyList<VoicePlanVoice>? cannedPlan = null)
                : base(
                    voiceAudio: Substitute.For<IVoiceAudioWriter>(),
                    transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                    voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                    transcriptionSettings: new FakeTranscriptionSettings(),
                    voiceDesignPromptService: new FakeVoiceDesignPromptService(cannedPrompt, throws),
                    fileSystem: Substitute.For<IFileSystem>())
            {
                _cannedPrompt = cannedPrompt;
                _throws = throws;
                _delayMs = delayMs;
                _audioFails = audioFails;
                _audioThrows = audioThrows;
                _cannedAudioFileName = cannedAudioFileName;
                _cannedTranscript = cannedTranscript;
                _cannedPlan = cannedPlan;
            }

            public override Task<string> BuildRenderedPromptAsync(string bookTitle, string author, string characterName) =>
                Task.FromResult($"[rendered: {characterName}]");

            public override async Task<IReadOnlyList<VoicePlanVoice>> GenerateVoicePlanAsync(
                string bookTitle, string author, string characterName, bool isNarrator = false,
                bool alsoNarrates = false, CancellationToken ct = default)
            {
                if (_delayMs > 0)
                    await Task.Delay(_delayMs, ct);
                ct.ThrowIfCancellationRequested();
                if (_throws)
                    throw new InvalidOperationException("Simulated LLM failure");
                return _cannedPlan ?? [new VoicePlanVoice("Default", "Covers the whole book", _cannedPrompt)];
            }

            public override async Task<string> GenerateWithPromptAsync(string renderedPrompt, CancellationToken ct = default)
            {
                if (_delayMs > 0)
                    await Task.Delay(_delayMs, ct);
                ct.ThrowIfCancellationRequested();
                if (_throws)
                    throw new InvalidOperationException("Simulated LLM failure");
                return _cannedPrompt;
            }

            public override async Task<VoiceGenerationResult> GenerateVoiceAudioAsync(
                VoiceGenerationRequest request, CancellationToken ct = default)
            {
                if (_delayMs > 0)
                    await Task.Delay(_delayMs, ct);
                ct.ThrowIfCancellationRequested();
                if (_audioThrows)
                    throw new InvalidOperationException("Simulated audio generation failure");
                if (_audioFails)
                    return VoiceGenerationResult.Failure("Simulated audio generation failure");
                return VoiceGenerationResult.Success(_cannedAudioFileName, _cannedTranscript);
            }
        }

        private sealed class SequencedFakeVoiceOrchestrator : VoiceOrchestrator
        {
            private readonly bool[] _shouldThrow;
            private readonly string _cannedPrompt;
            private int _callIndex;

            public SequencedFakeVoiceOrchestrator(bool[] shouldThrow, string cannedPrompt)
                : base(
                    voiceAudio: Substitute.For<IVoiceAudioWriter>(),
                    transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                    voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                    transcriptionSettings: new FakeTranscriptionSettings(),
                    voiceDesignPromptService: new FakeVoiceDesignPromptService(cannedPrompt, throws: false),
                    fileSystem: Substitute.For<IFileSystem>())
            {
                _shouldThrow = shouldThrow;
                _cannedPrompt = cannedPrompt;
            }

            public override Task<string> BuildRenderedPromptAsync(string bookTitle, string author, string characterName) =>
                Task.FromResult($"[rendered: {characterName}]");

            public override Task<string> GenerateWithPromptAsync(string renderedPrompt, CancellationToken ct = default)
            {
                var idx = Interlocked.Increment(ref _callIndex) - 1;
                if (idx < _shouldThrow.Length && _shouldThrow[idx])
                    throw new InvalidOperationException($"Simulated failure at call {idx}");
                return Task.FromResult(_cannedPrompt);
            }

            public override Task<IReadOnlyList<VoicePlanVoice>> GenerateVoicePlanAsync(
                string bookTitle, string author, string characterName, bool isNarrator = false,
                bool alsoNarrates = false, CancellationToken ct = default)
            {
                var idx = Interlocked.Increment(ref _callIndex) - 1;
                if (idx < _shouldThrow.Length && _shouldThrow[idx])
                    throw new InvalidOperationException($"Simulated failure at call {idx}");
                return Task.FromResult<IReadOnlyList<VoicePlanVoice>>(
                    [new VoicePlanVoice("Default", "Covers the whole book", _cannedPrompt)]);
            }
        }

        private sealed class SequencedFakeAudioOrchestrator : VoiceOrchestrator
        {
            private readonly bool[] _shouldFail;
            private readonly bool[] _shouldThrow;
            private readonly string _cannedAudioFileName;
            private readonly string _cannedTranscript;
            private int _callIndex;

            public SequencedFakeAudioOrchestrator(
                bool[] shouldFail,
                string cannedAudioFileName,
                string cannedTranscript,
                bool[]? shouldThrow = null)
                : base(
                    voiceAudio: Substitute.For<IVoiceAudioWriter>(),
                    transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                    voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                    transcriptionSettings: new FakeTranscriptionSettings(),
                    voiceDesignPromptService: new FakeVoiceDesignPromptService("prompt", throws: false),
                    fileSystem: Substitute.For<IFileSystem>())
            {
                _shouldFail = shouldFail;
                _shouldThrow = shouldThrow ?? Array.Empty<bool>();
                _cannedAudioFileName = cannedAudioFileName;
                _cannedTranscript = cannedTranscript;
            }

            public override Task<string> BuildRenderedPromptAsync(string bookTitle, string author, string characterName) =>
                Task.FromResult($"[rendered: {characterName}]");

            public override Task<VoiceGenerationResult> GenerateVoiceAudioAsync(
                VoiceGenerationRequest request, CancellationToken ct = default)
            {
                var idx = Interlocked.Increment(ref _callIndex) - 1;
                if (idx < _shouldThrow.Length && _shouldThrow[idx])
                    throw new InvalidOperationException($"Simulated throw at call {idx}");
                if (idx < _shouldFail.Length && _shouldFail[idx])
                    return Task.FromResult(VoiceGenerationResult.Failure($"Simulated failure at call {idx}"));
                return Task.FromResult(VoiceGenerationResult.Success(_cannedAudioFileName, _cannedTranscript));
            }
        }

        private sealed class FakeTranscriptionSettings : TranscriptionSettingsService
        {
            public FakeTranscriptionSettings() : base(null!, null!) { }
            public override Task<Read2Me.AppData.Entities.TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult<Read2Me.AppData.Entities.TranscriptionServiceConfig?>(null);
        }

        private sealed class FakeVoiceDesignPromptService : VoiceDesignPromptService
        {
            private readonly string _prompt;
            private readonly bool _throws;

            public FakeVoiceDesignPromptService(string prompt, bool throws = false)
                : base(null!, null!, null!, null!)
            {
                _prompt = prompt;
                _throws = throws;
            }

            public override Task<GenerateResult> GenerateWithPromptAsync(string renderedPrompt, CancellationToken ct = default)
            {
                if (_throws)
                    return Task.FromResult(new GenerateResult(GenerateStatus.Failed, null, "Simulated failure"));
                return Task.FromResult(new GenerateResult(GenerateStatus.Success, _prompt, null));
            }
        }
    }
}
