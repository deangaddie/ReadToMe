using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Voice;

namespace Read2Me.Services.Audio
{
    /// <summary>One row in the recent-audio picker: a generated paragraph item and its provenance.</summary>
    public sealed record RecentAudioSample(
        string FolderName,
        string ProjectTitle,
        Guid ParagraphItemId,
        string AudioRelativePath,
        string Text,
        string? CharacterName,
        string? VoiceName);

    public interface IRecentAudioSampleFinder
    {
        /// <summary>
        /// The most recently generated paragraph-item audio across every project, newest first.
        /// </summary>
        Task<IReadOnlyList<RecentAudioSample>> FindAsync(int limit, CancellationToken ct = default);
    }

    /// <summary>
    /// Finds recent paragraph audio for the consonant-soften A/B preview picker.
    /// Recency comes from the WAV's last-write time — a generated item carries no timestamp of its
    /// own, and the file is rewritten on every regeneration, so its mtime <em>is</em> "when this
    /// audio was made". Files are stat'd first so only the winning handful hit a project DB.
    /// </summary>
    public class RecentAudioSampleFinder(
        IFileSystem fs,
        IProjectCatalogReader catalog,
        IAudioItemReader items,
        IVoiceResolver voices) : IRecentAudioSampleFinder
    {
        public async Task<IReadOnlyList<RecentAudioSample>> FindAsync(int limit, CancellationToken ct = default)
        {
            if (limit <= 0) return [];

            var recent = catalog.GetProjects()
                .SelectMany(folder => fs
                    .ListFiles(Path.Combine(fs.GetProjectFolderPath(folder), "audio"), "*.wav")
                    .Select(f => (Folder: folder, File: f)))
                .Where(x => Guid.TryParse(Path.GetFileNameWithoutExtension(x.File.Path), out _))
                .OrderByDescending(x => x.File.LastWriteTimeUtc)
                .Take(limit)
                .ToList();

            if (recent.Count == 0) return [];

            var titles = (await catalog.GetProjectSummariesAsync())
                .ToDictionary(s => s.FolderName, s => s.Title);

            var byFolder = new Dictionary<string, (Dictionary<Guid, AudioSampleInfo> Rows, IReadOnlyDictionary<Guid, string?> Voices)>();

            foreach (var group in recent.GroupBy(x => x.Folder))
            {
                var folderId = new ProjectFolderId(group.Key);
                var ids = group
                    .Select(x => Guid.Parse(Path.GetFileNameWithoutExtension(x.File.Path)))
                    .ToList();

                var rows = await items.GetAudioSampleInfosAsync(folderId, ids);
                var voiceNames = await voices.ResolveNamesAsync(folderId, ids, ct);
                byFolder[group.Key] = (rows.ToDictionary(r => r.ParagraphItemId), voiceNames);
            }

            var samples = new List<RecentAudioSample>();
            foreach (var (folder, file) in recent)
            {
                var itemId = Guid.Parse(Path.GetFileNameWithoutExtension(file.Path));
                var (rows, voiceNames) = byFolder[folder];

                // A WAV whose item row is gone (deleted item) is an orphan file, not a sample.
                if (!rows.TryGetValue(itemId, out var row)) continue;

                samples.Add(new RecentAudioSample(
                    folder,
                    titles.TryGetValue(folder, out var title) ? title : folder,
                    itemId,
                    row.AudioRelativePath,
                    row.Text,
                    row.CharacterName,
                    voiceNames.TryGetValue(itemId, out var voice) ? voice : null));
            }

            return samples;
        }
    }
}
