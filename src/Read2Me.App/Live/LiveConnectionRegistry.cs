using System.Collections.Concurrent;

namespace Read2Me.App.Live;

/// <summary>
/// Which projects and streams each hub connection has joined. SignalR groups do the routing; this
/// registry answers the two questions groups cannot: "which folders does the relay need to compute
/// status for right now" and "which folders go in this connection's snapshot".
/// </summary>
public sealed class LiveConnectionRegistry
{
    private sealed class Membership
    {
        public readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Streams = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, Membership> _connections = new();

    public void Connected(string connectionId) => _connections.TryAdd(connectionId, new Membership());

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void JoinProject(string connectionId, string folder) =>
        Mutate(connectionId, m => m.Folders.Add(folder));

    public void LeaveProject(string connectionId, string folder) =>
        Mutate(connectionId, m => m.Folders.Remove(folder));

    public void JoinStream(string connectionId, string stream) =>
        Mutate(connectionId, m => m.Streams.Add(stream));

    public void LeaveStream(string connectionId, string stream) =>
        Mutate(connectionId, m => m.Streams.Remove(stream));

    /// <summary>Folders this connection has joined (a copy).</summary>
    public IReadOnlyCollection<string> FoldersOf(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var m)) return [];
        lock (m) return m.Folders.ToArray();
    }

    /// <summary>Every folder at least one connection has joined (a copy).</summary>
    public IReadOnlyCollection<string> ActiveFolders()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _connections.Values)
            lock (m) result.UnionWith(m.Folders);
        return result;
    }

    public int ConnectionCount => _connections.Count;

    /// <summary>Connections currently in a stream group — the debug figure ticket 07 verifies join/leave against.</summary>
    public int StreamMemberCount(string stream)
    {
        var count = 0;
        foreach (var m in _connections.Values)
            lock (m) if (m.Streams.Contains(stream)) count++;
        return count;
    }

    /// <summary>Connections currently in a project group.</summary>
    public int ProjectMemberCount(string folder)
    {
        var count = 0;
        foreach (var m in _connections.Values)
            lock (m) if (m.Folders.Contains(folder)) count++;
        return count;
    }

    private void Mutate(string connectionId, Action<Membership> change)
    {
        var m = _connections.GetOrAdd(connectionId, _ => new Membership());
        lock (m) change(m);
    }
}
