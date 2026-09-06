using System.Collections.Concurrent;
using Read2Me.Core.Models;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Serializes writes per project, and only per project: two writers for the same Book commit in a
/// deterministic order, while two writers for different Books never wait on each other.
/// <para>
/// Serialization is a latency cost paid by a user gesture that arrives behind a background queue
/// write, so waiting is bounded rather than assumed. A caller that cannot acquire the lock inside
/// its budget is told so and reports an expected conflict, instead of holding a Blazor circuit open
/// indefinitely.
/// </para>
/// </summary>
public sealed class ProjectWriteLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits up to <paramref name="budget"/> for the project's write lock. Returns null if the
    /// budget ran out — the caller must not write. Dispose the handle to release.
    /// </summary>
    public async Task<IDisposable?> AcquireAsync(ProjectFolderId folderId, TimeSpan budget, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(folderId.Value, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(budget, ct))
            return null;
        return new Handle(gate);
    }

    private sealed class Handle(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
