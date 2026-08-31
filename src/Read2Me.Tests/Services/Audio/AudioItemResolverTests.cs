using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioItemResolverTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly FakeFileSystem _fs;
        private readonly ProjectDbContextProvider _dbProvider;
        private readonly FakeVoiceResolver _voiceResolver;
        private readonly FakeTtsSettingsService _ttsSettings;
        private readonly FakeAudioProcessingSettings _processingSettings;
        private readonly FakeVoiceDesignSettingsService _voiceDesignSettings;
        private readonly IAudioItemResolver _sut;

        private static readonly ParagraphTtsServiceConfig ActiveConfig = new()
        {
            Id = 1,
            Name = "Test",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = "{}"
        };

        public AudioItemResolverTests()
        {
            _folder = new ProjectFolderId(FolderName);
            _fs = new FakeFileSystem(TempDir);
            _fs.SeedFolder(FolderName);

            _dbProvider = new ProjectDbContextProvider();
            _voiceResolver = new FakeVoiceResolver();
            _ttsSettings = new FakeTtsSettingsService(ActiveConfig);
            _processingSettings = new FakeAudioProcessingSettings(ffmpegPath: "ffmpeg", werThreshold: 0.15);
            _voiceDesignSettings = new FakeVoiceDesignSettingsService("the sample text");

            _sut = new AudioItemResolver(
                _fs, _dbProvider, _voiceResolver, _ttsSettings, _processingSettings, _voiceDesignSettings,
                NullLogger<AudioItemResolver>.Instance);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<(QueuedAudioItem queued, Guid itemId, Guid? voiceId)> SeedCharacterItemAsync(
            bool hasVoice = true,
            bool hasRefAudio = true,
            bool hasCharacter = true,
            bool resolverReturnsVoice = true,
            string voiceAudioFile = "voices/char1/voice.wav",
            string text = "In a hole in the ground",
            string? voiceTranscript = null)
        {
            var charId = Guid.NewGuid();
            var character = new Data.Entities.Character { Id = charId, Name = "Bilbo" };

            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("bilbo", character);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddRawItem("item", ParagraphItemType.Speech,
                    text, hasCharacter ? charId : null))))
                .BuildAsync();

            // Item VoiceInstructions needs post-build update
            await using var db = await OpenDbAsync();
            var item = await db.ParagraphItems.FindAsync(b.ItemId("item"));
            item!.VoiceInstructions = "whispered";

            Guid? seededVoiceId = null;
            if (hasVoice)
            {
                var voice = new VoiceEntity
                {
                    Id = Guid.NewGuid(),
                    CharacterId = charId,
                    Name = "Bilbo Voice",
                    Source = VoiceSource.Uploaded,
                    AudioFileName = hasRefAudio ? voiceAudioFile : null,
                    Transcript = voiceTranscript
                };
                db.Voices.Add(voice);
                seededVoiceId = voice.Id;

                if (hasRefAudio)
                {
                    var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceAudioFile.Replace('/', Path.DirectorySeparatorChar));
                    _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);
                }
            }
            await db.SaveChangesAsync();

            if (hasVoice && resolverReturnsVoice)
                _voiceResolver.SetVoice(b.ItemId("item"), seededVoiceId);
            else if (!resolverReturnsVoice)
                _voiceResolver.SetVoice(b.ItemId("item"), null);

            var partId = (await db.Chapters.FindAsync(b.ChapterId("ch")))?.PartId ?? throw new InvalidOperationException("Part not found");
            var volId = b.VolumeId("vol");
            var itemRef = new AudioItemRef(b.ItemId("item"), b.ParagraphId("para"), b.ChapterId("ch"), partId, volId);
            return (new QueuedAudioItem(_folder, itemRef), b.ItemId("item"), seededVoiceId);
        }

        private async Task<(QueuedAudioItem queued, Guid itemId)> SeedNarrationItemAsync(
            bool hasNarratorVoice = true,
            bool resolverReturnsVoice = true,
            string voiceAudioFile = "voices/narrator/voice.wav")
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "The narrator spoke"))))
                .BuildAsync();

            await using var db = await OpenDbAsync();

            Guid? seededVoiceId = null;
            if (hasNarratorVoice)
            {
                var voice = new VoiceEntity
                {
                    Id = Guid.NewGuid(),
                    CharacterId = ProjectDbContext.NarratorId,
                    Name = "Narrator Voice",
                    Source = VoiceSource.Uploaded,
                    AudioFileName = voiceAudioFile
                };
                db.Voices.Add(voice);
                seededVoiceId = voice.Id;
                await db.SaveChangesAsync();

                var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceAudioFile.Replace('/', Path.DirectorySeparatorChar));
                _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);
            }

            if (hasNarratorVoice && resolverReturnsVoice)
                _voiceResolver.SetVoice(b.ItemId("item"), seededVoiceId);
            else if (!resolverReturnsVoice)
                _voiceResolver.SetVoice(b.ItemId("item"), null);

            var partId = (await db.Chapters.FindAsync(b.ChapterId("ch")))?.PartId ?? throw new InvalidOperationException("Part not found");
            var volId = b.VolumeId("vol");
            var itemRef = new AudioItemRef(b.ItemId("item"), b.ParagraphId("para"), b.ChapterId("ch"), partId, volId);
            return (new QueuedAudioItem(_folder, itemRef), b.ItemId("item"));
        }

        [Fact]
        public async Task CharacterItem_WithVoice_ReturnsSuccessWithPipelineRequest()
        {
            var (queued, _, _) = await SeedCharacterItemAsync();

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("Bilbo", result.Speaker);
            Assert.Equal("In a hole in the ground", result.SourceText);
            Assert.NotNull(result.Request);
            Assert.Equal("whispered", result.Request!.VoiceInstructions);
            Assert.Null(result.FailureReason);
        }

        [Fact]
        public async Task NarrationItem_WithNarratorVoice_ReturnsSuccessWithNarratorSpeaker()
        {
            var (queued, _) = await SeedNarrationItemAsync();

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("Narrator", result.Speaker);
            Assert.NotNull(result.Request);
        }

        [Fact]
        public async Task ItemNotFound_ReturnsFailureWithNullSpeakerAndText()
        {
            var missing = Guid.NewGuid();
            var itemRef = new AudioItemRef(missing, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var queued = new QueuedAudioItem(_folder, itemRef);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Speaker);
            Assert.Null(result.SourceText);
            Assert.Contains(missing.ToString(), result.FailureReason);
        }

        [Fact]
        public async Task NoCharacterAssigned_ReturnsFailure()
        {
            var (queued, _, _) = await SeedCharacterItemAsync(hasCharacter: false, resolverReturnsVoice: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Speaker);
            Assert.Contains("No character", result.FailureReason);
        }

        [Fact]
        public async Task NoVoice_ReturnsFailureWithCharacterName()
        {
            var (queued, _, _) = await SeedCharacterItemAsync(hasVoice: false, resolverReturnsVoice: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("Bilbo", result.FailureReason);
        }

        [Fact]
        public async Task ResolverReturnsNull_ReturnsNoDefaultVoiceFailure()
        {
            var (queued, _, _) = await SeedCharacterItemAsync(hasVoice: true, resolverReturnsVoice: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("No default voice", result.FailureReason);
            Assert.Contains("Bilbo", result.FailureReason);
        }

        [Fact]
        public async Task VoiceHasNoRefAudio_ReturnsFailureWithVoiceName()
        {
            var (queued, _, _) = await SeedCharacterItemAsync(hasVoice: true, hasRefAudio: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("no reference audio", result.FailureReason);
        }

        [Fact]
        public async Task NoTtsConfig_ReturnsFailure()
        {
            var (queued, _, _) = await SeedCharacterItemAsync();
            var noConfigResolver = new AudioItemResolver(
                _fs, _dbProvider, _voiceResolver,
                new FakeTtsSettingsService(null!),
                _processingSettings,
                _voiceDesignSettings,
                NullLogger<AudioItemResolver>.Instance);

            var result = await noConfigResolver.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("TTS", result.FailureReason);
        }

        [Fact]
        public async Task NarratorOnlyMode_CharacterItem_UsesNarratorVoice()
        {
            const string voiceFile = "voices/narrator/voice.wav";

            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddRawItem("item", ParagraphItemType.Speech, "He said something", null))))
                .BuildAsync();

            Guid narratorVoiceId;
            await using (var db = await OpenDbAsync())
            {
                var narratorVoice = new VoiceEntity
                {
                    Id = Guid.NewGuid(),
                    CharacterId = ProjectDbContext.NarratorId,
                    Name = "Narrator",
                    Source = VoiceSource.Uploaded,
                    AudioFileName = voiceFile
                };
                db.Voices.Add(narratorVoice);
                narratorVoiceId = narratorVoice.Id;
                await db.SaveChangesAsync();
            }

            var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceFile.Replace('/', Path.DirectorySeparatorChar));
            _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);

            _voiceResolver.SetVoice(b.ItemId("item"), narratorVoiceId);

            await using var db2 = await OpenDbAsync();
            var partId = (await db2.Chapters.FindAsync(b.ChapterId("ch")))!.PartId;
            var itemRef = new AudioItemRef(b.ItemId("item"), b.ParagraphId("para"), b.ChapterId("ch"), partId, b.VolumeId("vol"));
            var queued = new QueuedAudioItem(_folder, itemRef);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(result.Speaker);
        }

        [Fact]
        public async Task PipelineRequest_HasCorrectSettings()
        {
            var (queued, _, _) = await SeedCharacterItemAsync();

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            var req = result.Request!;
            Assert.Equal("ffmpeg", req.FfmpegPath);
            Assert.Equal(0.15, req.WerThreshold);
            Assert.Equal(1, req.MaxAttempts);
            Assert.Equal(ActiveConfig, req.TtsConfig);
        }

        [Fact]
        public async Task PipelineRequest_ReferenceTranscript_FallsBackToVoiceDesignSampleText()
        {
            var (queued, _, _) = await SeedCharacterItemAsync();

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("the sample text", result.Request!.ReferenceTranscript);
        }

        [Fact]
        public async Task PipelineRequest_ReferenceTranscript_PrefersVoiceTranscript()
        {
            var (queued, _, _) = await SeedCharacterItemAsync(voiceTranscript: "the voice's own transcript");

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("the voice's own transcript", result.Request!.ReferenceTranscript);
        }

        // ── narrator link (slice 13) ───────────────────────────────────────────

        /// <summary>Narration item in a book whose narrator is linked to "Dr. Watson".</summary>
        private async Task<(QueuedAudioItem queued, Guid watsonId)> SeedLinkedNarrationItemAsync(
            bool hasVoice = true)
        {
            const string voiceFile = "voices/watson/voice.wav";
            var watson = new Data.Entities.Character { Id = Guid.NewGuid(), Name = "Dr. Watson" };

            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("watson", watson);
            b.WithNarratorLink(watson.Id);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "The narrator spoke"))))
                .BuildAsync();

            await using var db = await OpenDbAsync();

            if (hasVoice)
            {
                var voice = new VoiceEntity
                {
                    Id = Guid.NewGuid(),
                    CharacterId = watson.Id,
                    Name = "Watson Voice",
                    Source = VoiceSource.Uploaded,
                    AudioFileName = voiceFile
                };
                db.Voices.Add(voice);
                await db.SaveChangesAsync();

                _fs.SeedFile(
                    Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceFile.Replace('/', Path.DirectorySeparatorChar)),
                    [0x52, 0x49, 0x46, 0x46]);
                _voiceResolver.SetVoice(b.ItemId("item"), voice.Id);
            }
            else
            {
                _voiceResolver.SetVoice(b.ItemId("item"), null);
            }

            var partId = (await db.Chapters.FindAsync(b.ChapterId("ch")))!.PartId;
            var itemRef = new AudioItemRef(b.ItemId("item"), b.ParagraphId("para"), b.ChapterId("ch"), partId, b.VolumeId("vol"));
            return (new QueuedAudioItem(_folder, itemRef), watson.Id);
        }

        [Fact]
        public async Task LinkedNarrator_NarrationItem_SpeakerIsLinkedCharacterName()
        {
            var (queued, _) = await SeedLinkedNarrationItemAsync();

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("Dr. Watson", result.Speaker);
            Assert.Equal("Dr. Watson", result.Request!.Speaker);
        }

        [Fact]
        public async Task LinkedNarrator_NarrationItem_NoVoice_FailureNamesLinkedCharacter()
        {
            var (queued, _) = await SeedLinkedNarrationItemAsync(hasVoice: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("No default voice for Dr. Watson", result.FailureReason);
        }

        [Fact]
        public async Task Unlinked_NarrationItem_NoVoice_FailureStillNamesNarrator()
        {
            var (queued, _) = await SeedNarrationItemAsync(hasNarratorVoice: false, resolverReturnsVoice: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("No default voice for Narrator", result.FailureReason);
        }

        // ── fakes ──────────────────────────────────────────────────────────────

        private sealed class FakeVoiceDesignSettingsService(string? sampleText)
            : VoiceDesignSettingsService(null!, NullLogger<VoiceDesignSettingsService>.Instance)
        {
            public override Task<string?> GetSampleTextAsync() => Task.FromResult(sampleText);
        }

        private sealed class FakeTtsSettingsService(ParagraphTtsServiceConfig config) : ParagraphTtsSettingsService(null!, null!)
        {
            public override Task<ParagraphTtsServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult<ParagraphTtsServiceConfig?>(config);
        }

        private sealed class FakeAudioProcessingSettings : AudioProcessingSettingsService
        {
            private readonly string? _ffmpegPath;
            private readonly double _werThreshold;
            private readonly int _audioMaxAttempts;

            public FakeAudioProcessingSettings(string? ffmpegPath, double werThreshold, int audioMaxAttempts = 1)
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance)
            {
                _ffmpegPath = ffmpegPath;
                _werThreshold = werThreshold;
                _audioMaxAttempts = audioMaxAttempts;
            }

            public override Task<AudioProcessingSettings> GetAsync() =>
                Task.FromResult(new AudioProcessingSettings(
                    _ffmpegPath, _werThreshold,
                    SentenceSplitEnabled: false, ChunkPauseMs: 300,
                    VolumePauseMs: 4000, PartPauseMs: 3000, ChapterPauseMs: 2500,
                    ParagraphPauseMs: 800, PauseMs: 500, AudioMaxAttempts: _audioMaxAttempts));
        }
    }
}
