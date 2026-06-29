using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Events;

namespace Read2Me.Services.Audio.Assembly
{
    public sealed class AudiobookAssemblyService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAudiobookEncoder _encoder;
        private readonly EventBroadcaster<AssemblyEvent> _broadcaster;
        private readonly IFileSystem _fs;
        private readonly ILogger<AudiobookAssemblyService> _logger;

        private readonly object _lock = new();
        private CancellationTokenSource? _cts;

        public bool IsRunning { get; private set; }
        public AssemblyPhase? CurrentPhase { get; private set; }
        public double EncodePercent { get; private set; }
        public string? LastError { get; private set; }
        public int AudioRemainingCount { get; private set; }

        public AudiobookAssemblyService(
            IServiceScopeFactory scopeFactory,
            IAudiobookEncoder encoder,
            EventBroadcaster<AssemblyEvent> broadcaster,
            IFileSystem fs,
            ILogger<AudiobookAssemblyService> logger)
        {
            _scopeFactory = scopeFactory;
            _encoder = encoder;
            _broadcaster = broadcaster;
            _fs = fs;
            _logger = logger;
        }

        /// <summary>
        /// Starts an assembly job. No-op if already running; returns false.
        /// When allowPartial is false (default), returns false if any non-pause items are missing audio.
        /// When allowPartial is true, missing non-pause items are filtered out of the manifest.
        /// </summary>
        public bool StartAsync(ProjectFolderId folder, bool allowPartial = false)
        {
            lock (_lock)
            {
                if (IsRunning) return false;

                IsRunning = true;
                LastError = null;
                EncodePercent = 0;
                CurrentPhase = null;

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                Task.Run(() => RunAsync(folder, allowPartial, ct));
            }
            return true;
        }

        public void Cancel()
        {
            lock (_lock)
                _cts?.Cancel();
        }

        private async Task RunAsync(ProjectFolderId folder, bool allowPartial, CancellationToken ct)
        {
            string? tmpPath = null;
            string? concatListPath = null;
            string? ffmetaPath = null;
            List<string>? silencePathsForCleanup = null;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var reader = scope.ServiceProvider.GetRequiredService<IProjectReader>();
                var settingsSvc = scope.ServiceProvider.GetRequiredService<AudioProcessingSettingsService>();
                var audioSettings = await settingsSvc.GetAsync();

                // ── Phase 1: Gather ───────────────────────────────────────────
                SetPhase(AssemblyPhase.Gather);
                ct.ThrowIfCancellationRequested();

                var rawManifest = await reader.GetAssemblyManifestAsync(folder, ct);

                var remaining = rawManifest.Count(e =>
                    !AudiobookAssemblyPlanner.IsPause(e.ItemType) && e.AudioRelativePath == null);

                if (remaining > 0 && !allowPartial)
                {
                    lock (_lock)
                    {
                        AudioRemainingCount = remaining;
                        IsRunning = false;
                        CurrentPhase = null;
                    }
                    return;
                }

                var manifest = allowPartial
                    ? AudiobookAssemblyPlanner.FilterPartialManifest(rawManifest)
                    : rawManifest;

                lock (_lock) { AudioRemainingCount = 0; }

                var project = await reader.GetProjectAsync(folder);
                var bookTitle = project?.BookTitle ?? folder.Value;
                var author = project?.Author ?? string.Empty;
                var coverRelPath = project?.CoverImage;
                var projectFolder = _fs.GetProjectFolderPath(folder.Value);

                // ── Phase 2: Silence ──────────────────────────────────────────
                SetPhase(AssemblyPhase.Silence);
                ct.ThrowIfCancellationRequested();

                var distinctPauseMs = manifest
                    .Where(e => AudiobookAssemblyPlanner.IsPause(e.ItemType))
                    .Select(e => AudiobookAssemblyPlanner.PauseMs(e.ItemType, audioSettings))
                    .Distinct()
                    .ToList();

                var silencePaths = new Dictionary<int, string>();
                foreach (var ms in distinctPauseMs)
                {
                    ct.ThrowIfCancellationRequested();
                    silencePaths[ms] = await _encoder.GetSilenceAsync(ms, audioSettings.FfmpegPath, ct);
                }
                silencePathsForCleanup = silencePaths.Values.ToList();

                // ── Phase 3: Probe / build concat ─────────────────────────────
                SetPhase(AssemblyPhase.ProbeConcat);
                ct.ThrowIfCancellationRequested();

                var concatEntries = AudiobookAssemblyPlanner.BuildConcatEntries(manifest, audioSettings);

                var audioDurations = new Dictionary<Guid, TimeSpan>();
                foreach (var entry in manifest)
                {
                    ct.ThrowIfCancellationRequested();

                    if (AudiobookAssemblyPlanner.IsPause(entry.ItemType))
                    {
                        audioDurations[entry.ParagraphItemId] =
                            TimeSpan.FromMilliseconds(AudiobookAssemblyPlanner.PauseMs(entry.ItemType, audioSettings));
                    }
                    else
                    {
                        var absPath = Path.Combine(projectFolder, entry.AudioRelativePath!);
                        audioDurations[entry.ParagraphItemId] =
                            await _encoder.GetDurationAsync(absPath, audioSettings.FfmpegPath, ct);
                    }
                }

                var absolutePaths = concatEntries.Select(ce => ce switch
                {
                    ConcatEntry.Audio a => Path.Combine(projectFolder, a.RelativePath),
                    ConcatEntry.Silence s => silencePaths[s.Milliseconds],
                    _ => throw new InvalidOperationException($"Unknown ConcatEntry: {ce}"),
                }).ToList();

                var chapters = AudiobookAssemblyPlanner.ComputeChapterTimestamps(manifest, audioDurations, audioSettings);
                var totalDuration = chapters.Count > 0 ? chapters[^1].End : TimeSpan.Zero;

                var runId = Guid.NewGuid().ToString("N");
                concatListPath = Path.Combine(Path.GetTempPath(), $"r2m-concat-{runId}.txt");
                ffmetaPath = Path.Combine(Path.GetTempPath(), $"r2m-meta-{runId}.txt");

                await File.WriteAllTextAsync(concatListPath, ConcatListBuilder.Build(absolutePaths), ct);
                await File.WriteAllTextAsync(ffmetaPath,
                    AudiobookAssemblyPlanner.GenerateFfmetadata(bookTitle, author, chapters), ct);

                var coverAbsPath = coverRelPath != null ? Path.Combine(projectFolder, coverRelPath) : null;

                // ── Phase 4: Encode ───────────────────────────────────────────
                SetPhase(AssemblyPhase.Encode);
                ct.ThrowIfCancellationRequested();

                var outputDir = Path.Combine(projectFolder, "output");
                Directory.CreateDirectory(outputDir);

                var outputSuffix = allowPartial
                    ? $"_partial_{DateTime.Today:yyyyMMdd}"
                    : string.Empty;
                var finalPath = Path.Combine(outputDir, SanitizeFileName(bookTitle) + outputSuffix + ".m4b");
                tmpPath = finalPath + ".tmp";

                var progress = new Progress<double>(f =>
                {
                    lock (_lock) { EncodePercent = f; }
                    _broadcaster.Publish(new AssemblyEncodeProgress(f));
                });

                await _encoder.EncodeAsync(
                    concatListPath, ffmetaPath, coverAbsPath,
                    tmpPath, totalDuration, progress,
                    audioSettings.FfmpegPath, ct);

                // ── Phase 5: Finalize ─────────────────────────────────────────
                SetPhase(AssemblyPhase.Finalize);

                File.Move(tmpPath, finalPath, overwrite: true);
                tmpPath = null;

                TryDelete(concatListPath); concatListPath = null;
                TryDelete(ffmetaPath); ffmetaPath = null;
                if (silencePathsForCleanup != null)
                    foreach (var sp in silencePathsForCleanup) TryDelete(sp);

                lock (_lock)
                {
                    IsRunning = false;
                    CurrentPhase = null;
                    EncodePercent = 1.0;
                }
                _broadcaster.Publish(new AssemblyCompleted());
            }
            catch (OperationCanceledException)
            {
                if (tmpPath != null) TryDelete(tmpPath);
                TryDelete(concatListPath);
                TryDelete(ffmetaPath);
                lock (_lock) { IsRunning = false; CurrentPhase = null; }
                _broadcaster.Publish(new AssemblyCancelled());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audiobook assembly failed for {Folder}", folder.Value);
                if (tmpPath != null) TryDelete(tmpPath);
                TryDelete(concatListPath);
                TryDelete(ffmetaPath);
                lock (_lock)
                {
                    IsRunning = false;
                    CurrentPhase = null;
                    LastError = ex.Message;
                }
                _broadcaster.Publish(new AssemblyFailed(ex.Message));
            }
        }

        private void SetPhase(AssemblyPhase phase)
        {
            lock (_lock) { CurrentPhase = phase; }
            _broadcaster.Publish(new AssemblyPhaseStarted(phase));
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        }

        private static void TryDelete(string? path)
        {
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
