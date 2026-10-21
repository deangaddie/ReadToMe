using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// The files that are part of <b>Project state</b> (ADR 0016, "Files"): each Voice's reference
/// audio and, once the voice audio editor has touched it, its stored original; the cover; and the
/// source book file. A step's <b>file list</b> maps each one's project-relative path to the sha256
/// of its content, kept in <c>history/blobs/</c>; a path not in the list didn't exist then.
/// <para>
/// A snapshot stores the whole list beside it (<c>{stepId:N}.files.json</c>, <c>base.files.json</c>)
/// and a chunk the paths its commit changed, each to its new hash or to null for gone, so a step's
/// list is its nearest snapshot's with the chunks after it applied — the same walk its rows take.
/// </para>
/// </summary>
internal static class StateFiles
{
    /// <summary>A file list or a chunk's changes: path to hash, case-insensitive like the file system.</summary>
    public static Dictionary<string, string?> NewList() => new(StringComparer.OrdinalIgnoreCase);

    public static string BlobsIn(string historyFolder) => Path.Combine(historyFolder, "blobs");

    public static string BlobOf(string historyFolder, string hash) => Path.Combine(BlobsIn(historyFolder), hash);

    /// <summary>The file list stored beside <paramref name="snapshot"/>.</summary>
    public static string ListBeside(string snapshot) => Path.ChangeExtension(snapshot, ".files.json");

    /// <summary>"voices\a.wav" or "/voices/a.wav" → "voices/a.wav": the form the rows name files in.</summary>
    public static string Normalise(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Every state file the rows name, by its path, with whose it is. A Voice's original is named
    /// only while the Voice has audio: it is the claim that the audio was edited.
    /// </summary>
    public static async Task<Dictionary<string, FileOwner>> NamedAsync(ProjectDbContext db, CancellationToken ct)
    {
        var named = new Dictionary<string, FileOwner>(StringComparer.OrdinalIgnoreCase);
        var voices = await db.Voices.AsNoTracking()
            .Where(v => v.AudioFileName != null && v.AudioFileName != "")
            .Select(v => new { v.Id, v.CharacterId, v.AudioFileName })
            .ToListAsync(ct);
        foreach (var voice in voices)
        {
            named[Normalise(voice.AudioFileName!)] = new FileOwner(FileKind.VoiceAudio, voice.Id);
            named[VoiceOriginalStore.PathOf(voice.CharacterId, voice.Id)] = new FileOwner(FileKind.VoiceOriginal, voice.Id);
        }

        var project = await db.Projects.AsNoTracking()
            .Select(p => new { p.CoverImage, p.Filename })
            .SingleOrDefaultAsync(ct);
        if (project?.Filename is { Length: > 0 } source)
            named[Normalise(source)] = new FileOwner(FileKind.Source, null);
        if (project?.CoverImage is { Length: > 0 } cover)
            named[Normalise(cover)] = new FileOwner(FileKind.Cover, null);
        return named;
    }

    /// <summary>
    /// The list of every named file that is on disk, each hashed into the blobs: what a snapshot
    /// taken when History starts records.
    /// </summary>
    public static async Task<Dictionary<string, string?>> ListAllAsync(
        ProjectDbContext db, string projectFolder, string historyFolder, CancellationToken ct)
    {
        var list = NewList();
        foreach (var path in (await NamedAsync(db, ct)).Keys)
            if (await StoreAsync(projectFolder, historyFolder, path, ct) is { } hash)
                list[path] = hash;
        return list;
    }

    /// <summary>
    /// What one commit changed, against <paramref name="tip"/>, the list before it: each file the
    /// rows stop naming is gone; each newly named one, and each <paramref name="declared"/> one the
    /// producer wrote over, is hashed into the blobs and recorded when its content differs. A file
    /// rewritten under a name it already had and not declared is someone else's staged write, and
    /// left for that commit to record. An original is never newly named on its own: it appears
    /// with an edit, which declares it.
    /// </summary>
    public static async Task<Dictionary<string, string?>> ChangesAsync(
        ProjectDbContext db, string projectFolder, string historyFolder, IReadOnlyDictionary<string, string?> tip,
        StateFileOwners declared, CancellationToken ct)
    {
        var named = await NamedAsync(db, ct);
        var changes = NewList();
        foreach (var path in tip.Keys.Where(p => !named.ContainsKey(p)))
            changes[path] = null;

        foreach (var (path, owner) in named)
        {
            var newlyNamed = !tip.ContainsKey(path) && owner.Kind != FileKind.VoiceOriginal;
            if (!newlyNamed && !owner.IsIn(declared))
                continue;
            var hash = await StoreAsync(projectFolder, historyFolder, path, ct);
            if (hash != tip.GetValueOrDefault(path))
                changes[path] = hash;
        }
        return changes;
    }

    /// <summary>Applies a chunk's changes to <paramref name="list"/>.</summary>
    public static void Apply(Dictionary<string, string?> list, Dictionary<string, string?> changes)
    {
        foreach (var (path, hash) in changes)
        {
            if (hash is null)
                list.Remove(path);
            else
                list[path] = hash;
        }
    }

    public static string Serialise(Dictionary<string, string?> files) => JsonSerializer.Serialize(files);

    public static Dictionary<string, string?> Deserialise(string json)
    {
        var list = NewList();
        foreach (var (path, hash) in JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [])
            list[path] = hash;
        return list;
    }

    /// <summary>The list beside <paramref name="snapshot"/>; empty when there is none to read.</summary>
    public static async Task<Dictionary<string, string?>> ReadListAsync(string snapshot, CancellationToken ct)
    {
        var file = ListBeside(snapshot);
        return File.Exists(file) ? Deserialise(await File.ReadAllTextAsync(file, ct)) : NewList();
    }

    /// <summary>Writes the list beside where <paramref name="snapshot"/> will be, whole or not at all.</summary>
    public static async Task WriteListAsync(string snapshot, Dictionary<string, string?> files, CancellationToken ct)
    {
        var target = ListBeside(snapshot);
        var pending = target + ".tmp";
        await File.WriteAllTextAsync(pending, Serialise(files), ct);
        File.Move(pending, target, overwrite: true);
    }

    /// <summary>
    /// Copies the file into the blobs under the sha256 of what was copied, once per hash, and
    /// answers the hash — or null when the file isn't on disk. The copy is hashed, not the file, so
    /// a write landing meanwhile can't put one content under another's hash. A copy left by a
    /// commit that then rolled back is a harmless orphan.
    /// </summary>
    private static async Task<string?> StoreAsync(string projectFolder, string historyFolder, string path, CancellationToken ct)
    {
        var source = Path.Combine(projectFolder, path);
        if (!File.Exists(source))
            return null;

        var blobs = BlobsIn(historyFolder);
        Directory.CreateDirectory(blobs);
        var pending = Path.Combine(blobs, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, pending);
            string hash;
            await using (var copy = File.OpenRead(pending))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(copy, ct));
            var blob = BlobOf(historyFolder, hash);
            if (!File.Exists(blob))
            {
                try { File.Move(pending, blob); }
                catch (IOException) when (File.Exists(blob)) { } // another commit stored the same content first
            }
            return hash;
        }
        finally
        {
            File.Delete(pending);
        }
    }
}

internal enum FileKind
{
    VoiceAudio,
    VoiceOriginal,
    Cover,
    Source,
}

/// <summary>Whose a state file is: a Voice's audio or original (with the Voice), the cover, the source.</summary>
internal readonly record struct FileOwner(FileKind Kind, Guid? VoiceId)
{
    public bool IsIn(StateFileOwners declared) => Kind switch
    {
        FileKind.VoiceAudio or FileKind.VoiceOriginal => declared.Voices.Contains(VoiceId!.Value),
        FileKind.Cover => declared.Cover,
        _ => false,
    };
}
