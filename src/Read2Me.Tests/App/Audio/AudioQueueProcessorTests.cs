using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
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
using Read2Me.Services.Audio.Transcription;
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
        private readonly FakeNormalizer _normalizer;
        private readonly FakeWerComparer _wer;
        private readonly FakeTranscriptionClient _transcriber;
        private readonly FakeTranscriptionResolver _transcriptionResolver;
        private readonly FakeTranscriptionSettings _transcriptionSettings;
        private readonly FakeAudioProcessingSettings _audioProcessingSettings;
        private readonly AudioReviewService _reviews;
        private readonly AudioGenBroadcaster _broadcaster;
        private readonly List<AudioGenEvent> _events = new();
        private readonly AudioQueueProcessor _sut;
        private readonly ProjectFolderId _folder;

        private static readonly ParagraphTtsServiceConfig ActiveConfig = new()
        {
            Id = 1,
            Name = "Test",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = "{}"
        };

        private static readonly TranscriptionServiceConfig TranscriptionConfig = new()
        {
            Id = 1,
            Name = "Whisper",
            Type = TranscriptionServiceType.LocalWhisper,
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

            // Defaults: normalize succeeds, transcript matches source (WER 0), threshold 0.15, config present.
            _normalizer = new FakeNormalizer();
            _wer = new FakeWerComparer(0.0);
            _transcriber = new FakeTranscriptionClient("In a hole in the ground");
            _transcriptionResolver = new FakeTranscriptionResolver(_transcriber);
            _transcriptionSettings = new FakeTranscriptionSettings(TranscriptionConfig);
            _audioProcessingSettings = new FakeAudioProcessingSettings(ffmpegPath: "ffmpeg", werThreshold: 0.15);
            _reviews = new AudioReviewService();
            _broadcaster = new AudioGenBroadcaster();
            _broadcaster.Event += e => _events.Add(e);

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
                _normalizer,
                _wer,
                _transcriptionResolver,
                _transcriptionSettings,
                _audioProcessingSettings,
                _reviews,
                _broadcaster,
                NullLogger<AudioQueueProcessor>.Instance);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private async Task<AudioReview?> ReviewRowAsync(Guid itemId)
        {
            await using var db = await OpenDbAsync();
            return await db.AudioReviews.FirstOrDefaultAsync(r => r.ParagraphItemId == itemId);
        }

        private async Task<(QueuedAudioItem item, Guid itemId)> SeedCharacterItemAsync(
            bool hasDefaultVoice = true,
            string voiceAudioFile = "voices/char1/voice.wav",
            string text = "In a hole in the ground")
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
        public async Task TrailingComma_IsReplacedWithSemicolon_BeforeTts()
        {
            var (queuedItem, _) = await SeedCharacterItemAsync(text: "Turning it into a greeting,");

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            Assert.Equal("Turning it into a greeting;", _ttsClient.LastText);
        }

        [Theory]
        [InlineData("Hello there,", "Hello there;")]
        [InlineData("Hello there,   ", "Hello there;")]
        [InlineData("Hello there.", "Hello there.")]
        [InlineData("Hello, there", "Hello, there")]
        [InlineData("", "")]
        [InlineData(",", ";")]
        public void ReplaceTrailingComma_HandlesCases(string input, string expected)
        {
            Assert.Equal(expected, AudioQueueProcessor.ReplaceTrailingComma(input));
        }

        [Fact]
        public async Task NarrationItem_WithNarratorVoice_GeneratesWavAndSetsAudioFileName()
        {
            var (queuedItem, itemId) = await SeedNarrationItemAsync();
            _transcriber.Transcript = "The narrator spoke";

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

        // --- Post-processing pipeline -------------------------------------------------

        [Fact]
        public async Task BothStagesPass_StoresAudio_NoReviewRow_ServiceCleared()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            // Seed a stale in-memory review to prove a pass clears it.
            _reviews.Set(_folder, itemId, new AudioReviewInfo(
                Read2Me.Core.Models.AudioReviewState.NeedsReview, false, "stale", false, 0.9, "stale", null, null));

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var expectedPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));

            Assert.Null(await ReviewRowAsync(itemId));
            Assert.Null(_reviews.ReviewOf(_folder, itemId));
            Assert.Null(_queue.OutcomeOf(_folder, itemId));
        }

        [Fact]
        public async Task WerOverThreshold_StoresAudio_RowVerifyFailedWithWerAndReason()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _wer.Result = 0.42; // > 0.15

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var expectedPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));

            var row = await ReviewRowAsync(itemId);
            Assert.NotNull(row);
            Assert.True(row!.NormalizeOk);
            Assert.False(row.VerifyOk);
            Assert.Equal(0.42, row.Wer);
            Assert.Equal("WER 0.42 > 0.15", row.VerifyReason);

            var info = _reviews.ReviewOf(_folder, itemId);
            Assert.NotNull(info);
            Assert.False(info!.VerifyOk);
            Assert.Equal(0.42, info.Wer);
        }

        [Fact]
        public async Task NormalizeSkipped_WithVerifyPass_StoresAudio_RowNormalizeFailedVerifyOk()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _normalizer.Result = (NormalizeStatus.Skipped, "ffmpeg failed: boom");

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var expectedPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));

            var row = await ReviewRowAsync(itemId);
            Assert.NotNull(row);
            Assert.False(row!.NormalizeOk);
            Assert.Equal("ffmpeg failed: boom", row.NormalizeReason);
            Assert.True(row.VerifyOk); // verify still ran on the original audio
        }

        [Fact]
        public async Task NormalizeSkipped_MissingFfmpeg_ReasonIsFfmpegNotFound()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _normalizer.Result = (NormalizeStatus.Skipped, "ffmpeg not found (set path in Audio Processing settings)");

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var row = await ReviewRowAsync(itemId);
            Assert.NotNull(row);
            Assert.False(row!.NormalizeOk);
            Assert.StartsWith("ffmpeg not found", row.NormalizeReason);
        }

        [Fact]
        public async Task NoTranscriptionConfig_StoresAudio_VerifyFailed_WerNull_NoConfigReason()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _transcriptionSettings.Config = null;

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var expectedPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));

            var row = await ReviewRowAsync(itemId);
            Assert.NotNull(row);
            Assert.False(row!.VerifyOk);
            Assert.Null(row.Wer);
            Assert.Equal("no transcription config", row.VerifyReason);
            Assert.False(_transcriber.WasCalled);
        }

        [Fact]
        public async Task TranscribeThrows_StoresAudio_VerifyFailed_CouldNotVerifyReason()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _transcriber.ThrowMessage = "service unavailable";

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var expectedPath = Path.Combine(_fs.GetProjectFolderPath(FolderName), "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));

            var row = await ReviewRowAsync(itemId);
            Assert.NotNull(row);
            Assert.False(row!.VerifyOk);
            Assert.Null(row.Wer);
            Assert.Equal("could not verify: service unavailable", row.VerifyReason);
        }

        [Fact]
        public async Task RegenerateAndPass_AfterPriorFailure_RemovesRow()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();

            // First pass fails verification, creating a row.
            _wer.Result = 0.42;
            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);
            Assert.NotNull(await ReviewRowAsync(itemId));

            // Re-process with both stages passing ⇒ row removed and service cleared.
            _wer.Result = 0.0;
            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            Assert.Null(await ReviewRowAsync(itemId));
            Assert.Null(_reviews.ReviewOf(_folder, itemId));
        }

        [Fact]
        public async Task StageFailure_NeverMarksFailed_AndMarksCompleteLast()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _normalizer.Result = (NormalizeStatus.Skipped, "ffmpeg failed: boom");
            _wer.Result = 0.42;

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            // No MarkFailed despite both stages failing.
            Assert.Null(_queue.OutcomeOf(_folder, itemId));
            // MarkComplete ran (audio version recorded).
            Assert.NotNull(_queue.AudioVersionOf(_folder, itemId));
        }

        // --- Audio Gen Stream events --------------------------------------------------

        [Fact]
        public async Task HappyPath_PublishesItemStartedAudioGeneratedNormalizedTranscribedVerified_InOrder()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var started = Assert.IsType<ItemStarted>(_events[0]);
            Assert.Equal(itemId, started.Id);
            Assert.Equal("Bilbo", started.Character);
            Assert.Equal("In a hole in the ground", started.Text);

            Assert.Equal(itemId, Assert.IsType<AudioGenerated>(_events[1]).Id);

            var normalized = Assert.IsType<Normalized>(_events[2]);
            Assert.True(normalized.Ok);

            Assert.Equal(itemId, Assert.IsType<Transcribed>(_events[3]).Id);

            var verified = Assert.IsType<Verified>(_events[4]);
            Assert.True(verified.Ok);

            Assert.DoesNotContain(_events, e => e is Failed);
        }

        [Fact]
        public async Task WerOverThreshold_PublishesVerifiedFalseWithWer_NoFailed()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _wer.Result = 0.42;

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var verified = _events.OfType<Verified>().Single();
            Assert.False(verified.Ok);
            Assert.Equal(0.42, verified.Wer);
            Assert.DoesNotContain(_events, e => e is Failed);
        }

        [Fact]
        public async Task NormalizeSkipped_PublishesNormalizedFalseWithReason_NoFailed()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync();
            _normalizer.Result = (NormalizeStatus.Skipped, "ffmpeg failed: boom");

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var normalized = _events.OfType<Normalized>().Single();
            Assert.False(normalized.Ok);
            Assert.Equal("ffmpeg failed: boom", normalized.Reason);
            Assert.DoesNotContain(_events, e => e is Failed);
        }

        [Fact]
        public async Task HardFail_CharacterKnown_NoDefaultVoice_PublishesItemStartedThenFailed_NoPhaseEvents()
        {
            var (queuedItem, itemId) = await SeedCharacterItemAsync(hasDefaultVoice: false);

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var started = Assert.IsType<ItemStarted>(_events[0]);
            Assert.Equal("Bilbo", started.Character);
            Assert.IsType<Failed>(_events[1]);
            Assert.Equal(2, _events.Count);
            Assert.DoesNotContain(_events, e => e is AudioGenerated or Normalized or Transcribed or Verified);
        }

        [Fact]
        public async Task HardFail_RowNotFound_PublishesItemStartedNullThenFailed()
        {
            var missing = Guid.NewGuid();
            var itemRef = new AudioItemRef(missing, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var queuedItem = new QueuedAudioItem(_folder, itemRef);

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var started = Assert.IsType<ItemStarted>(_events[0]);
            Assert.Equal(missing, started.Id);
            Assert.Null(started.Character);
            Assert.Null(started.Text);
            Assert.IsType<Failed>(_events[1]);
        }

        [Fact]
        public async Task NarrationItem_ItemStartedReportsNarratorAsSpeaker()
        {
            var (queuedItem, itemId) = await SeedNarrationItemAsync();
            _transcriber.Transcript = "The narrator spoke";

            await _sut.ProcessItemAsync(queuedItem, CancellationToken.None);

            var started = Assert.IsType<ItemStarted>(_events[0]);
            Assert.Equal("Narrator", started.Character);
        }

        // --- Fakes --------------------------------------------------------------------

        private sealed class FakeTtsClient : IParagraphTtsClient
        {
            public bool WasCalled { get; private set; }
            public string? LastVoiceInstructions { get; private set; }
            public string? LastText { get; private set; }

            public Task<Stream> GenerateAsync(string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, CancellationToken ct = default)
            {
                WasCalled = true;
                LastVoiceInstructions = voiceInstructions;
                LastText = text;
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

        private sealed class FakeNormalizer : IAudioNormalizer
        {
            // Default: normalized passthrough.
            public (NormalizeStatus Status, string? Reason) Result { get; set; } = (NormalizeStatus.Normalized, null);

            public async Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default)
            {
                var ms = new MemoryStream();
                if (wav.CanSeek) wav.Position = 0;
                await wav.CopyToAsync(ms, ct);
                ms.Position = 0;
                return new NormalizeResult(Result.Status, ms, Result.Reason);
            }
        }

        private sealed class FakeWerComparer : IWerComparer
        {
            public double Result { get; set; }
            public FakeWerComparer(double result) => Result = result;
            public double Compute(string reference, string hypothesis) => Result;
        }

        private sealed class FakeTranscriptionClient : ITranscriptionClient
        {
            public FakeTranscriptionClient(string transcript) => Transcript = transcript;
            public string Transcript { get; set; }
            public string? ThrowMessage { get; set; }
            public bool WasCalled { get; private set; }

            public Task<string> TranscribeAsync(TranscriptionServiceConfig config, Stream audio, string fileName,
                CancellationToken ct = default)
            {
                WasCalled = true;
                if (ThrowMessage is not null)
                    throw new InvalidOperationException(ThrowMessage);
                return Task.FromResult(Transcript);
            }
        }

        private sealed class FakeTranscriptionResolver(ITranscriptionClient client) : ITranscriptionClientResolver
        {
            public ITranscriptionClient Resolve(TranscriptionServiceType type) => client;
        }

        private sealed class FakeTranscriptionSettings(TranscriptionServiceConfig? config)
            : TranscriptionSettingsService(null!, NullLogger<TranscriptionSettingsService>.Instance)
        {
            public TranscriptionServiceConfig? Config { get; set; } = config;
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult(Config);
        }

        private sealed class FakeAudioProcessingSettings : AudioProcessingSettingsService
        {
            private readonly string? _ffmpegPath;
            private readonly double _werThreshold;

            public FakeAudioProcessingSettings(string? ffmpegPath, double werThreshold)
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance)
            {
                _ffmpegPath = ffmpegPath;
                _werThreshold = werThreshold;
            }

            public override Task<AudioProcessingSettings> GetAsync() =>
                Task.FromResult(new AudioProcessingSettings(
                    _ffmpegPath, _werThreshold,
                    SentenceSplitEnabled: false, SentencePauseMs: 300, SentenceMinChunkChars: 15));
        }
    }
}
