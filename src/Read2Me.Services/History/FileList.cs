using System.Text.Json;

namespace Read2Me.Services.History;

/// <summary>
/// A step's <b>file list</b> (<see cref="StateFiles"/>): each state file's project-relative path to
/// the sha256 of its content in the blobs; a path not in the list didn't exist then. Paths are
/// case-insensitive, like the file system. Stored as a JSON object beside a snapshot.
/// </summary>
internal sealed class FileList
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Paths => _files.Keys;

    public bool Contains(string path) => _files.ContainsKey(path);

    /// <summary>The hash of <paramref name="path"/>'s content, or null when it isn't in the list.</summary>
    public string? HashOf(string path) => _files.GetValueOrDefault(path);

    public void Set(string path, string hash) => _files[path] = hash;

    /// <summary>Applies a chunk's changes: each path to its new hash, or out of the list when gone.</summary>
    public void Apply(FileChanges changes)
    {
        foreach (var (path, hash) in changes)
        {
            if (hash is null)
                _files.Remove(path);
            else
                _files[path] = hash;
        }
    }

    public string Serialise() => JsonSerializer.Serialize(_files);

    public static FileList Read(string json)
    {
        var list = new FileList();
        foreach (var (path, hash) in JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [])
            if (hash is not null)
                list.Set(path, hash);
        return list;
    }
}

/// <summary>
/// What one commit changed in the file list, as its chunk records it: each changed path to its new
/// hash, or to null for gone. Case-insensitive, like <see cref="FileList"/>.
/// </summary>
internal sealed class FileChanges : IEnumerable<KeyValuePair<string, string?>>
{
    private readonly Dictionary<string, string?> _changes = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _changes.Count;

    public void Set(string path, string? hash) => _changes[path] = hash;

    /// <summary>What takes the list <paramref name="from"/> to <paramref name="to"/>: each path whose hash differs, or that is gone.</summary>
    public static FileChanges Between(FileList from, FileList to)
    {
        var changes = new FileChanges();
        foreach (var path in from.Paths.Where(p => !to.Contains(p)))
            changes.Set(path, null);
        foreach (var path in to.Paths.Where(p => to.HashOf(p) != from.HashOf(p)))
            changes.Set(path, to.HashOf(path));
        return changes;
    }

    public string Serialise() => JsonSerializer.Serialize(_changes);

    public static FileChanges Read(string json)
    {
        var changes = new FileChanges();
        foreach (var (path, hash) in JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [])
            changes.Set(path, hash);
        return changes;
    }

    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => _changes.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
