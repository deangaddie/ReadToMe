using Read2Me.Core.Models;
using Read2Me.Services.Voice;

namespace Read2Me.Services.Audio
{
    /// <summary>One row in the recent-audio picker: a generated paragraph item and its provenance.</summary>
    public sealed record RecentAudioSample(
        ProjectFolderId Folder,
        string ProjectTitle,
        Guid ParagraphItemId,
        string Text,
        string? CharacterName,
        string? VoiceName);

    public interface IRecentAudioSampleFinder
    {
        /// <summary>
        /// The most recently generated paragraph items that still hold a <b>Preview Source</b>, newest first.
        /// </summary>
        Task<IReadOnlyList<RecentAudioSample>> FindAsync(int limit, CancellationToken ct = default);
    }

    /// <summary>
    /// Finds samples for the consonant-soften A/B preview picker. An item is offerable only while its
    /// Preview Source is still cached, so the cache — not the project audio folders — is the list to
    /// walk: it is already capped, already global, and already ordered by generation time. Only the
    /// winning handful hit a project DB.
    /// </summary>
    public class RecentAudioSampleFinder(
        IPreviewSourceCache previewSources,
        IProjectCatalogReader catalog,
        IAudioItemReader items,
        IVoiceResolver voices) : IRecentAudioSampleFinder
    {
        public async Task<IReadOnlyList<RecentAudioSample>> FindAsync(int limit, CancellationToken ct = default)
        {
            if (limit <= 0) return [];

            var recent = previewSources.List().Take(limit).ToList();
            if (recent.Count == 0) return [];

            var titles = (await catalog.GetProjectSummariesAsync())
                .ToDictionary(s => s.FolderName, s => s.Title);

            // The cache outlives the projects it caches for, so a deleted project can still have
            // entries. Drop them before anyone tries to open a DB that is no longer there.
            recent = recent.Where(e => titles.ContainsKey(e.Folder.Value)).ToList();
            if (recent.Count == 0) return [];

            var byFolder = new Dictionary<string, (Dictionary<Guid, AudioSampleInfo> Rows, IReadOnlyDictionary<Guid, string?> Voices)>();

            foreach (var group in recent.GroupBy(e => e.Folder))
            {
                var ids = group.Select(e => e.ParagraphItemId).ToList();

                var rows = await items.GetAudioSampleInfosAsync(group.Key, ids);
                var voiceNames = await voices.ResolveNamesAsync(group.Key, ids, ct);
                byFolder[group.Key.Value] = (rows.ToDictionary(r => r.ParagraphItemId), voiceNames);
            }

            var samples = new List<RecentAudioSample>();
            foreach (var entry in recent)
            {
                var (rows, voiceNames) = byFolder[entry.Folder.Value];

                // A Preview Source whose item row is gone (deleted item) is an orphan file, not a sample.
                if (!rows.TryGetValue(entry.ParagraphItemId, out var row)) continue;

                samples.Add(new RecentAudioSample(
                    entry.Folder,
                    titles[entry.Folder.Value],
                    entry.ParagraphItemId,
                    row.Text,
                    row.CharacterName,
                    voiceNames.TryGetValue(entry.ParagraphItemId, out var voice) ? voice : null));
            }

            return samples;
        }
    }
}
