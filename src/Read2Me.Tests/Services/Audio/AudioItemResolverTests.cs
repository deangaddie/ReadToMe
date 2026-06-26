using FractionalIndexing;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly ProjectReader _projectReader;
        private readonly FakeTtsSettingsService _ttsSettings;
        private readonly FakeAudioProcessingSettings _processingSettings;
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

            var services = new ServiceCollection();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _dbProvider = new ProjectDbContextProvider();
            var fileSystem = new Read2Me.Services.IO.FileSystemService(
                Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var dbSession = new ProjectDbSession(fileSystem, _dbProvider, NullLogger<ProjectDbSession>.Instance);
            _projectReader = new ProjectReader(dbSession, NullLogger<ProjectReader>.Instance);

            _ttsSettings = new FakeTtsSettingsService(ActiveConfig);
            _processingSettings = new FakeAudioProcessingSettings(ffmpegPath: "ffmpeg", werThreshold: 0.15);

            _sut = new AudioItemResolver(_fs, _dbProvider, _projectReader, _ttsSettings, _processingSettings);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<(QueuedAudioItem queued, Guid itemId)> SeedCharacterItemAsync(
            bool hasVoice = true,
            bool hasRefAudio = true,
            bool hasCharacter = true,
            string voiceAudioFile = "voices/char1/voice.wav",
            string text = "In a hole in the ground")
        {
            await using var db = await OpenDbAsync();

            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "Bilbo" };
            db.Characters.Add(character);

            if (hasVoice)
            {
                var voice = new VoiceEntity
                {
                    Id = Guid.NewGuid(),
                    CharacterId = charId,
                    Name = "Bilbo Voice",
                    Source = VoiceSource.Uploaded,
                    AudioFileName = hasRefAudio ? voiceAudioFile : null
                };
                db.Voices.Add(voice);
                db.VoiceRules.Add(new VoiceRule
                {
                    Id = Guid.NewGuid(),
                    CharacterId = charId,
                    VoiceId = voice.Id,
                    IsDefault = true,
                    Rank = Key(),
                });
                if (hasRefAudio)
                {
                    var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceAudioFile.Replace('/', Path.DirectorySeparatorChar));
                    _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);
                }
            }

            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(), CharacterId = charId };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character,
                CharacterId = hasCharacter ? charId : null,
                Text = text,
                VoiceInstructions = "whispered",
                Order = Key()
            };

            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();

            var itemRef = new AudioItemRef(item.Id, para.Id, ch.Id, part.Id, vol.Id);
            return (new QueuedAudioItem(_folder, itemRef), item.Id);
        }

        private async Task<(QueuedAudioItem queued, Guid itemId)> SeedNarrationItemAsync(
            bool hasNarratorVoice = true,
            string voiceAudioFile = "voices/narrator/voice.wav")
        {
            await using var db = await OpenDbAsync();

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
                db.VoiceRules.Add(new VoiceRule
                {
                    Id = Guid.NewGuid(),
                    CharacterId = ProjectDbContext.NarratorId,
                    VoiceId = voice.Id,
                    IsDefault = true,
                    Rank = Key(),
                });
                var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceAudioFile.Replace('/', Path.DirectorySeparatorChar));
                _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);
            }

            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = para.Id,
                ItemType = ParagraphItemType.Narration,
                Text = "The narrator spoke",
                Order = Key()
            };

            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();

            var itemRef = new AudioItemRef(item.Id, para.Id, ch.Id, part.Id, vol.Id);
            return (new QueuedAudioItem(_folder, itemRef), item.Id);
        }

        [Fact]
        public async Task CharacterItem_WithVoice_ReturnsSuccessWithPipelineRequest()
        {
            var (queued, _) = await SeedCharacterItemAsync();

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
            var (queued, _) = await SeedCharacterItemAsync(hasCharacter: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            // CharacterId is null on the item → Include yields no Character → speaker is null
            Assert.Null(result.Speaker);
            Assert.Contains("No character", result.FailureReason);
        }

        [Fact]
        public async Task NoVoice_ReturnsFailureWithCharacterName()
        {
            var (queued, _) = await SeedCharacterItemAsync(hasVoice: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("Bilbo", result.FailureReason);
        }

        [Fact]
        public async Task VoiceHasNoRefAudio_ReturnsFailureWithVoiceName()
        {
            var (queued, _) = await SeedCharacterItemAsync(hasVoice: true, hasRefAudio: false);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("no reference audio", result.FailureReason);
        }

        [Fact]
        public async Task NoTtsConfig_ReturnsFailure()
        {
            var (queued, _) = await SeedCharacterItemAsync();
            var noConfigResolver = new AudioItemResolver(
                _fs, _dbProvider, _projectReader,
                new FakeTtsSettingsService(null!),
                _processingSettings);

            var result = await noConfigResolver.ResolveAsync(queued, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("TTS", result.FailureReason);
        }

        [Fact]
        public async Task NarratorOnlyMode_CharacterItem_UsesNarratorSpeaker()
        {
            await using var db = await OpenDbAsync();

            db.Projects.Add(new Project
            {
                Id = Guid.NewGuid(),
                Title = "Test",
                BookTitle = "Book",
                Author = "Author",
                Filename = "test.txt",
                Type = BookFileType.Text,
                NarratorOnlyMode = true
            });

            const string voiceFile = "voices/narrator/voice.wav";
            var voice = new VoiceEntity
            {
                Id = Guid.NewGuid(),
                CharacterId = ProjectDbContext.NarratorId,
                Name = "Narrator",
                Source = VoiceSource.Uploaded,
                AudioFileName = voiceFile
            };
            db.Voices.Add(voice);
            db.VoiceRules.Add(new VoiceRule
            {
                Id = Guid.NewGuid(),
                CharacterId = ProjectDbContext.NarratorId,
                VoiceId = voice.Id,
                IsDefault = true,
                Rank = Key()
            });
            var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceFile.Replace('/', Path.DirectorySeparatorChar));
            _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);

            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character,
                CharacterId = null,
                Text = "He said something",
                Order = Key()
            };
            db.Volumes.AddRange(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();

            var itemRef = new AudioItemRef(item.Id, para.Id, ch.Id, part.Id, vol.Id);
            var queued = new QueuedAudioItem(_folder, itemRef);

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            // NarratorOnly mode routes to narrator voice; speaker label comes from item's Character which is null here
            Assert.Null(result.Speaker);
        }

        [Fact]
        public async Task PipelineRequest_HasCorrectSettings()
        {
            var (queued, _) = await SeedCharacterItemAsync();

            var result = await _sut.ResolveAsync(queued, CancellationToken.None);

            Assert.True(result.Succeeded);
            var req = result.Request!;
            Assert.Equal("ffmpeg", req.FfmpegPath);
            Assert.Equal(0.15, req.WerThreshold);
            Assert.Equal(1, req.MaxAttempts);
            Assert.Equal(ActiveConfig, req.TtsConfig);
        }

        // ── fakes ──────────────────────────────────────────────────────────────

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
