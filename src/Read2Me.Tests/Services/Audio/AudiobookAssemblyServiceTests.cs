using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudiobookAssemblyServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _folderName = "test-book";

        public AudiobookAssemblyServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "R2mAssemblyTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private ProjectFolderId Folder => new(_folderName);

        private static AssemblyManifestEntry AudioEntry(
            Guid id, string path,
            Guid? volId = null, Guid? partId = null, Guid? chapId = null,
            string? volTitle = null, string? chapTitle = null) =>
            new(id, ParagraphItemType.Narration, path,
                volId ?? Guid.NewGuid(), volTitle,
                partId ?? Guid.NewGuid(), null,
                chapId ?? Guid.NewGuid(), chapTitle);

        private static AssemblyManifestEntry PauseEntry(
            ParagraphItemType kind,
            Guid? volId = null, Guid? partId = null, Guid? chapId = null) =>
            new(Guid.NewGuid(), kind, null,
                volId ?? Guid.NewGuid(), null,
                partId ?? Guid.NewGuid(), null,
                chapId ?? Guid.NewGuid(), null);

        private static AssemblyManifestEntry MissingAudioEntry(
            Guid? volId = null, Guid? partId = null, Guid? chapId = null) =>
            new(Guid.NewGuid(), ParagraphItemType.Narration, null,
                volId ?? Guid.NewGuid(), null,
                partId ?? Guid.NewGuid(), null,
                chapId ?? Guid.NewGuid(), null);

        private static AudioProcessingSettings DefaultSettings() => new(
            FfmpegPath: null,
            WerThreshold: 0.15,
            SentenceSplitEnabled: false,
            ChunkPauseMs: 300,
            VolumePauseMs: 4000,
            PartPauseMs: 3000,
            ChapterPauseMs: 2500,
            ParagraphPauseMs: 800,
            PauseMs: 500);

        private Harness BuildHarness(
            IReadOnlyList<AssemblyManifestEntry>? manifest = null,
            bool encodeThrows = false,
            int encodeDelayMs = 0,
            CancellationTokenSource? encodeCts = null)
        {
            var fs = new FakeFileSystem(_tempDir);
            fs.SeedFolder(_folderName);

            var encoder = new FakeEncoder(
                durationMs: 1000,
                silencePath: Path.Combine(_tempDir, "silence.wav"),
                encodeThrows: encodeThrows,
                encodeDelayMs: encodeDelayMs,
                encodeCts: encodeCts);

            var broadcaster = new AudiobookAssemblyBroadcaster();
            var events = new List<AssemblyEvent>();
            broadcaster.Event += e => { lock (events) events.Add(e); };

            var fakeReader = new FakeProjectReader(manifest ?? new List<AssemblyManifestEntry>());
            var fakeSettings = new FakeAssemblySettings();

            var services = new ServiceCollection();
            services.AddSingleton<IProjectReader>(fakeReader);
            services.AddSingleton<AudioProcessingSettingsService>(fakeSettings);
            var sp = services.BuildServiceProvider();

            var sut = new AudiobookAssemblyService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                encoder,
                broadcaster,
                fs,
                NullLogger<AudiobookAssemblyService>.Instance);

            return new Harness(sut, encoder, events, fakeReader);
        }

        private static async Task WaitForIdleAsync(AudiobookAssemblyService sut, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (sut.IsRunning && DateTime.UtcNow < deadline)
                await Task.Delay(20);
        }

        // ── Single-flight ─────────────────────────────────────────────────────

        [Fact]
        public async Task StartAsync_WhileRunning_IsNoOp()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            // Use slow encode so second call arrives while first is still running.
            var h = BuildHarness(manifest, encodeDelayMs: 2000);

            // Write the audio file so precondition passes.
            var audioDir = Path.Combine(_tempDir, _folderName, "audio");
            Directory.CreateDirectory(audioDir);
            await File.WriteAllBytesAsync(Path.Combine(audioDir, "a.wav"), new byte[4]);

            var r1 = h.Sut.StartAsync(Folder);
            var r2 = h.Sut.StartAsync(Folder); // second call while first is queued

            Assert.True(r1, "first start must succeed");
            Assert.False(r2, "second start while running must return false");

            h.Sut.Cancel(); // clean up
            await WaitForIdleAsync(h.Sut);
        }

        // ── Precondition ──────────────────────────────────────────────────────

        [Fact]
        public async Task StartAsync_MissingAudio_RefusesToStartSetsAudioRemainingCount()
        {
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(Guid.NewGuid(), "audio/a.wav"),
                MissingAudioEntry(),
            };
            var h = BuildHarness(manifest);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Equal(1, h.Sut.AudioRemainingCount);
            Assert.False(h.Sut.IsRunning);
            Assert.Equal(0, h.Encoder.EncodeCallCount);
        }

        [Fact]
        public async Task StartAsync_AllAudioPresent_ProceedsToEncode()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest);

            // Write the audio file.
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Equal(0, h.Sut.AudioRemainingCount);
            Assert.Equal(1, h.Encoder.EncodeCallCount);
        }

        // ── Manifest snapshot ─────────────────────────────────────────────────

        [Fact]
        public async Task StartAsync_UsesManifestSnapshotAtStart_LaterEditsIgnored()
        {
            var item1 = Guid.NewGuid();
            var originalManifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            // Use a slow encode so we can swap the reader manifest while the job is in the encode phase.
            var h = BuildHarness(originalManifest, encodeDelayMs: 500);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);

            // Wait until Gather/Silence/Probe phases are done (but encode is still running).
            await Task.Delay(100);

            // Swap the reader's manifest — must not affect the already-snapshotted run.
            h.FakeReader.Manifest = new List<AssemblyManifestEntry>
            {
                MissingAudioEntry(),
            };

            await WaitForIdleAsync(h.Sut, timeoutMs: 5000);

            // Encode was still called (original manifest had audio, snapshot was taken at start).
            Assert.Equal(1, h.Encoder.EncodeCallCount);
            Assert.Contains(h.Events, e => e is AssemblyCompleted);
        }

        // ── Phase order ───────────────────────────────────────────────────────

        [Fact]
        public async Task StartAsync_Success_BroadcastsPhasesInOrder()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            var phases = h.Events
                .OfType<AssemblyPhaseStarted>()
                .Select(e => e.Phase)
                .ToList();

            Assert.Equal(new[]
            {
                AssemblyPhase.Gather,
                AssemblyPhase.Silence,
                AssemblyPhase.ProbeConcat,
                AssemblyPhase.Encode,
                AssemblyPhase.Finalize,
            }, phases);

            Assert.Contains(h.Events, e => e is AssemblyCompleted);
        }

        [Fact]
        public async Task StartAsync_Success_ForwardsEncodeProgress()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            var progressEvents = h.Events.OfType<AssemblyEncodeProgress>().ToList();
            Assert.NotEmpty(progressEvents);
            Assert.Contains(progressEvents, p => p.Fraction >= 1.0);
        }

        // ── Output paths + atomic rename ──────────────────────────────────────

        [Fact]
        public async Task StartAsync_Success_WritesM4bAndRemovesTmp()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            var outputDir = Path.Combine(_tempDir, _folderName, "output");
            var m4b = Directory.GetFiles(outputDir, "*.m4b").FirstOrDefault();
            var tmp = Directory.GetFiles(outputDir, "*.tmp").FirstOrDefault();

            Assert.NotNull(m4b);
            Assert.Null(tmp);
            Assert.EndsWith(".m4b", m4b);
        }

        [Fact]
        public async Task StartAsync_Success_EncodestoTmpPathThenRenames()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            // Encoder must have been called with a .tmp path.
            Assert.EndsWith(".tmp", h.Encoder.LastOutputPath);
        }

        // ── Hard failure ──────────────────────────────────────────────────────

        [Fact]
        public async Task StartAsync_EncodeThrows_BroadcastsFailedEventAndClearsIsRunning()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest, encodeThrows: true);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.False(h.Sut.IsRunning);
            Assert.Contains(h.Events, e => e is AssemblyFailed);
            Assert.NotNull(h.Sut.LastError);
        }

        [Fact]
        public async Task StartAsync_EncodeThrows_RemovesTmpFile()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest, encodeThrows: true);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            var tmpPath = h.Encoder.LastOutputPath;
            Assert.False(File.Exists(tmpPath), "tmp file must be deleted on failure");
        }

        // ── Cancellation ──────────────────────────────────────────────────────

        [Fact]
        public async Task Cancel_StopsJob_BroadcastsCancelledAndClearsIsRunning()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest, encodeDelayMs: 5000);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            // Brief wait so the run enters the encode phase.
            await Task.Delay(100);
            h.Sut.Cancel();

            await WaitForIdleAsync(h.Sut, timeoutMs: 6000);

            Assert.False(h.Sut.IsRunning);
            Assert.Null(h.Sut.LastError);
            Assert.Contains(h.Events, e => e is AssemblyCancelled);
            Assert.DoesNotContain(h.Events, e => e is AssemblyFailed);
        }

        [Fact]
        public async Task Cancel_RemovesTmpFile()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            // Use a CTS that the encoder respects so it can be cancelled.
            var h = BuildHarness(manifest, encodeDelayMs: 5000);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await Task.Delay(100);
            h.Sut.Cancel();

            await WaitForIdleAsync(h.Sut, timeoutMs: 6000);

            var tmpPath = h.Encoder.LastOutputPath;
            if (tmpPath != null)
                Assert.False(File.Exists(tmpPath), "tmp file must be deleted on cancel");
        }

        // ── Null-cover pass-through ────────────────────────────────────────────

        [Fact]
        public async Task StartAsync_NullCover_SucceedsWithoutCoverArg()
        {
            var item1 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(item1, "audio/a.wav"),
            };
            var h = BuildHarness(manifest);
            Directory.CreateDirectory(Path.Combine(_tempDir, _folderName, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(_tempDir, _folderName, "audio", "a.wav"), new byte[4]);

            h.Sut.StartAsync(Folder);
            await WaitForIdleAsync(h.Sut);

            Assert.Equal(1, h.Encoder.EncodeCallCount);
            Assert.Null(h.Encoder.LastCoverPath);
            Assert.Contains(h.Events, e => e is AssemblyCompleted);
        }

        // ── Fakes ─────────────────────────────────────────────────────────────

        private sealed record Harness(
            AudiobookAssemblyService Sut,
            FakeEncoder Encoder,
            List<AssemblyEvent> Events,
            FakeProjectReader FakeReader);

        private sealed class FakeEncoder : IAudiobookEncoder
        {
            private readonly int _durationMs;
            private readonly string _silencePath;
            private readonly bool _encodeThrows;
            private readonly int _encodeDelayMs;
            private readonly CancellationTokenSource? _encodeCts;

            public int EncodeCallCount { get; private set; }
            public string? LastOutputPath { get; private set; }
            public string? LastCoverPath { get; private set; }

            public FakeEncoder(int durationMs, string silencePath, bool encodeThrows = false,
                int encodeDelayMs = 0, CancellationTokenSource? encodeCts = null)
            {
                _durationMs = durationMs;
                _silencePath = silencePath;
                _encodeThrows = encodeThrows;
                _encodeDelayMs = encodeDelayMs;
                _encodeCts = encodeCts;
            }

            public Task<TimeSpan> GetDurationAsync(string wavPath, string? ffmpegPath, CancellationToken ct = default)
                => Task.FromResult(TimeSpan.FromMilliseconds(_durationMs));

            public Task<string> GetSilenceAsync(int ms, string? ffmpegPath, CancellationToken ct = default)
            {
                // Create a stub file so the concat list has a real path.
                File.WriteAllBytes(_silencePath, new byte[4]);
                return Task.FromResult(_silencePath);
            }

            public async Task EncodeAsync(
                string concatListPath,
                string ffmetadataPath,
                string? coverImagePath,
                string outputPath,
                TimeSpan totalDuration,
                IProgress<double>? progress,
                string? ffmpegPath,
                CancellationToken ct = default)
            {
                EncodeCallCount++;
                LastOutputPath = outputPath;
                LastCoverPath = coverImagePath;

                if (_encodeDelayMs > 0)
                    await Task.Delay(_encodeDelayMs, ct);

                ct.ThrowIfCancellationRequested();

                if (_encodeThrows)
                {
                    // Create the tmp file so cleanup path can be tested.
                    await File.WriteAllBytesAsync(outputPath, new byte[4], CancellationToken.None);
                    throw new InvalidOperationException("Simulated encode failure");
                }

                // Write stub output and report full progress.
                await File.WriteAllBytesAsync(outputPath, new byte[4], ct);
                progress?.Report(1.0);
            }
        }

        private sealed class FakeProjectReader : IProjectReader
        {
            public IReadOnlyList<AssemblyManifestEntry> Manifest { get; set; }

            public FakeProjectReader(IReadOnlyList<AssemblyManifestEntry> manifest)
                => Manifest = manifest;

            public Task<IReadOnlyList<AssemblyManifestEntry>> GetAssemblyManifestAsync(
                ProjectFolderId folder, CancellationToken ct)
                => Task.FromResult(Manifest);

            public Task<Project?> GetProjectAsync(ProjectFolderId folderId)
                => Task.FromResult<Project?>(new Project
                {
                    Id = Guid.NewGuid(),
                    Title = _folderName,
                    BookTitle = "Test Book",
                    Author = "Test Author"
                });

            // All other members throw — tests only exercise what the service uses.
            public IReadOnlyList<string> GetProjects() => throw new NotImplementedException();
            public Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync() => throw new NotImplementedException();
            public Task<bool> HasBookContentAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<BookOverview> GetBookOverviewAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId) => throw new NotImplementedException();
            public Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId) => throw new NotImplementedException();
            public Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId) => throw new NotImplementedException();
            public Task<HierarchyChildren> GetChildrenAsync(ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId) => throw new NotImplementedException();
            public Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<Read2Me.Data.Entities.Voice>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId) => throw new NotImplementedException();
            public Task<Guid?> GetDefaultVoiceIdAsync(ProjectFolderId folderId, Guid characterId) => throw new NotImplementedException();
            public Task<(StoryPosition ItemPosition, IReadOnlyList<Read2Me.Services.Voice.RuleInput> Rules)> GetVoiceRuleInputsAsync(ProjectFolderId folderId, Guid itemId, Guid characterId) => throw new NotImplementedException();
            public Task<List<VoiceRuleRow>> GetCharacterVoiceRulesAsync(ProjectFolderId folderId, Guid characterId) => throw new NotImplementedException();
            public Task<IReadOnlyDictionary<Guid, string?>> GetResolvedVoiceNamesAsync(ProjectFolderId folderId, IEnumerable<Guid> itemIds, bool narratorOnlyMode) => throw new NotImplementedException();
            public Task<List<CharacterLine>> GetCharacterLinesAsync(ProjectFolderId folderId, Guid characterId) => throw new NotImplementedException();
            public Task<int> GetTotalPartCountAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<CharacterParagraphRef>> GetCharacterParagraphsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = false) => throw new NotImplementedException();
            public Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<(Guid ParagraphId, string Preview)>> GetOrderedParagraphsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphIds) => throw new NotImplementedException();
            public Task<List<AudioItemRef>> GetAudioItemRefsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool needsAudioOnly = false, bool narratorOnlyMode = false) => throw new NotImplementedException();
            public Task<List<AudioItemRef>> GetOrderedAudioItemRefsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphItemIds) => throw new NotImplementedException();
            public Task<IReadOnlyDictionary<Guid, int>> GetNodeAudioItemCountsAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<List<(Guid ParagraphItemId, AudioReviewInfo Info)>> GetAudioReviewsAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<IReadOnlyList<Read2Me.Services.NodeStatus.ParagraphStatusSeedRow>> GetNodeStatusSeedAsync(ProjectFolderId folderId) => throw new NotImplementedException();
            public Task<ParagraphContext?> GetParagraphContextAsync(ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after) => throw new NotImplementedException();

            private static readonly string _folderName = "test-book";
        }

        private sealed class FakeAssemblySettings : AudioProcessingSettingsService
        {
            public FakeAssemblySettings()
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance) { }

            public override Task<AudioProcessingSettings> GetAsync() =>
                Task.FromResult(DefaultSettings());

            private static AudioProcessingSettings DefaultSettings() => new(
                FfmpegPath: null,
                WerThreshold: 0.15,
                SentenceSplitEnabled: false,
                ChunkPauseMs: 300,
                VolumePauseMs: 4000,
                PartPauseMs: 3000,
                ChapterPauseMs: 2500,
                ParagraphPauseMs: 800,
                PauseMs: 500);
        }
    }
}
