using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.Live;

/// <summary>
/// Reads the per-project status maps the hub sends: node roll-ups, per-paragraph attribution
/// status and per-item audio status. Everything here is a pull over singleton state
/// (<see cref="NodeStatusService"/>, both queues, the review mirror); the relay diffs consecutive
/// reads and sends only what changed, the hub sends the whole thing on join.
/// </summary>
public sealed class ProjectStatusSource(
    NodeStatusService nodes,
    CharacterQueueService attribution,
    AudioQueueService audio,
    AudioReviewService reviews,
    BookRevisionSequence revisions)
{
    public ProjectSnapshot Snapshot(ProjectFolderId folder) =>
        new(folder.Value, revisions.Current(folder), Nodes(folder), nodes.AudioRemainingForFolder(folder),
            Paragraphs(folder), Items(folder));

    public int FolderAudioRemaining(ProjectFolderId folder) => nodes.AudioRemainingForFolder(folder);

    public Dictionary<string, NodeStatusSummary?> Nodes(ProjectFolderId folder)
    {
        var result = new Dictionary<string, NodeStatusSummary?>();
        foreach (var id in nodes.NodeIds(folder))
            result[id.ToString()] = nodes.StatusForNode(folder, id);
        return result;
    }

    public Dictionary<string, ParagraphStatusEntry?> Paragraphs(ProjectFolderId folder)
    {
        var result = new Dictionary<string, ParagraphStatusEntry?>();
        foreach (var id in attribution.KnownParagraphs(folder))
        {
            var entry = new ParagraphStatusEntry(attribution.StatusOf(folder, id), attribution.OutcomeOf(folder, id));
            if (entry.Status is not null || entry.Outcome is not null)
                result[id.ToString()] = entry;
        }
        return result;
    }

    public Dictionary<string, ItemStatusEntry?> Items(ProjectFolderId folder)
    {
        var result = new Dictionary<string, ItemStatusEntry?>();
        var ids = new HashSet<Guid>(audio.KnownItems(folder));
        ids.UnionWith(reviews.ItemIds(folder));
        foreach (var id in ids)
        {
            var entry = new ItemStatusEntry(
                audio.StatusOf(folder, id), audio.OutcomeOf(folder, id), audio.AudioVersionOf(folder, id),
                reviews.ReviewOf(folder, id));
            if (entry.Status is not null || entry.Outcome is not null || entry.AudioVersion is not null || entry.Review is not null)
                result[id.ToString()] = entry;
        }
        return result;
    }

    /// <summary>
    /// Entries that differ between two reads: changed or added keys carry the new value, keys that
    /// vanished carry null so the client drops them.
    /// </summary>
    public static Dictionary<string, TValue> Diff<TValue>(
        IReadOnlyDictionary<string, TValue> last, IReadOnlyDictionary<string, TValue> current)
    {
        var delta = new Dictionary<string, TValue>();
        foreach (var (key, value) in current)
            if (!last.TryGetValue(key, out var previous) || !EqualityComparer<TValue>.Default.Equals(previous, value))
                delta[key] = value;
        foreach (var key in last.Keys)
            if (!current.ContainsKey(key))
                delta[key] = default!;
        return delta;
    }
}
