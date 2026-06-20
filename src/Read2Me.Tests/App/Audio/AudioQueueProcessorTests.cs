using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Audio;
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

namespace Read2Me.Tests.App.Audio
{
    public class AudioQueueProcessorTests : ProjectDbTestBase
    {
        private readonly AudioQueueService _queue;
        private readonly FakeTtsClient _ttsClient;
        private readonly FakeTtsClientResolver _resolver;
        private readonly FakeTtsSettingsService _settings;
        private readonly BookCommandHandler _commands;
        private readonly FakeFileSystem _fs;
        private readonly AudioQueueProcessor _sut;
        private readonly ProjectFolderId _folder;

        private static readonly ParagraphTtsServiceConfig ActiveConfig = new()
        {
            Id = 1,
            Name = "Test",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = "{}"
        };

        public AudioQueueProcessorTests()
        {
            _folder = new ProjectFolderId(FolderName);
            _queue = new AudioQueueService();
            _ttsClient = new FakeTtsClient();
            _resolver = new FakeTtsClientResolver(_ttsClient);
            _settings = new FakeTtsSettingsService(ActiveConfig);
            _fs = new FakeFileSystem(TempDir);
            _fs.SeedFolder(FolderName);

            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();
            _commands = sp.GetRequiredService<BookCommandHandler>();

            _sut = new AudioQueueProcessor(
                _queue,
                _settings,
                _resolver,
                _commands,
                _fs,
                new ProjectDbContextProvider(),
                NullLogger<AudioQueueProcessor>.Instance);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<(QueuedAudioItem item, Guid itemId)> SeedCharacterItemAsync(
            bool hasDefaultVoice = true,
            string voiceAudioFile = "voices/char1/voice.wav")
        {
            await using var db = await OpenDbAsync();

            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "Bilbo" };
            db.Characters.Add(character);

            if (hasDefaultVoice)
            {
                var voice = new Voice
                {
                    Id = Guid.NewGuid(),
                    CharacterId = charId,
                    Name = "Bilbo Voice",
                    IsDefault = true,
                    Source = VoiceSource.Uploaded,
                    AudioFileName = voiceAudioFile
                };
                db.Voices.Add(voice);
                // Seed the reference audio file so it can be opened
                var fullPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), voiceAudioFile.Replace('/', Path.DirectorySeparatorChar));
                _fs.SeedFile(fullPath, [0x52, 0x49, 0x46, 0x46]);
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
                CharacterId = charId,
                Text = "In a hole in the ground",
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

        private async Task<(QueuedAudioItem item, Guid itemId)> SeedNarrationItemAsync(
            bool hasNarratorVoice = true,
            string voiceAudioFile = "voices/narrator/voice.wav")
        {
            await using var db = await OpenDbAsync();

            if (hasNarratorVoice)
            {
                var voice = new Voice
                {
                    Id = Guid.NewGuid(),
                    CharacterId = ProjectDbContext.NarratorId,
                    Name = "Narrator Voice",
                    IsDefault = true,
                    Source = VoiceSource.Uploaded,
                    AudioFileName = voiceAudioFile
                };
                db.Voices.Add(voice);
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
        public async Task CharacterItem_WithDefaultVoice_GeneratesWavAndSetsAudioFileName()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            Assert.True(_ttsClient.WasCalled);
            Assert.Equal("whispered", _ttsClient.LastVoiceInstructions);

            await using var db = await OpenDbAsync();
            var updated = await db.ParagraphItems.FindAsync(itemId);
            Assert.Equal($"audio/{itemId}.wav", updated!.AudioFileName);

            var expectedPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));

            Assert.Null(_queue.OutcomeOf(_folder, itemId));
        }

        [Fact]
        public async Task NarrationItem_WithNarratorVoice_GeneratesWavAndSetsAudioFileName()
        {
            var (queuedItem, itemId) = await SeedNarrationItemAsync();

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            Assert.True(_ttsClient.WasCalled);

            await using var db = await OpenDbAsync();
            var updated = await db.ParagraphItems.FindAsync(itemId);
            Assert.Equal($"audio/{itemId}.wav", updated!.AudioFileName);
        }

        [Fact]
        public async Task CharacterItem_NoDefaultVoice_MarksFailed_NoBilboName_NoTtsCall()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync(hasDefaultVoice: false);

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            Assert.False(_ttsClient.WasCalled);

            var outcome = _queue.OutcomeOf(_folder, itemId);
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Failed, outcome.Kind);
            Assert.Contains("Bilbo", outcome.Reason);

            await using var db = await OpenDbAsync();
            var item = await db.ParagraphItems.FindAsync(itemId);
            Assert.Null(item!.AudioFileName);
        }

        [Fact]
        public async Task NarrationItem_NoNarratorVoice_MarksFailed_NoTtsCall()
        {
            var (queuedItem, itemId) = await SeedNarrationItemAsync(hasNarratorVoice: false);

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            Assert.False(_ttsClient.WasCalled);

            var outcome = _queue.OutcomeOf(_folder, itemId);
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Failed, outcome.Kind);

            await using var db = await OpenDbAsync();
            var item = await db.ParagraphItems.FindAsync(itemId);
            Assert.Null(item!.AudioFileName);
        }

        private sealed class FakeTtsClient : IParagraphTtsClient
        {
            public bool WasCalled { get; private set; }
            public string? LastVoiceInstructions { get; private set; }

            public Task<Stream> GenerateAsync(string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, CancellationToken ct = default)
            {
                WasCalled = true;
                LastVoiceInstructions = voiceInstructions;
                Stream result = new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00]);
                return Task.FromResult(result);
            }
        }

        private sealed class FakeTtsClientResolver(IParagraphTtsClient client) : IParagraphTtsClientResolver
        {
            public IParagraphTtsClient Resolve(ParagraphTtsServiceType type) => client;
        }

        private sealed class FakeTtsSettingsService(ParagraphTtsServiceConfig config) : ParagraphTtsSettingsService(null!, null!)
        {
            public override Task<ParagraphTtsServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult<ParagraphTtsServiceConfig?>(config);
        }
    }
}
