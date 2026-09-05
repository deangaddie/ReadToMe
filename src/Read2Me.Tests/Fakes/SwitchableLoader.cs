using Read2Me.App.State;
using Read2Me.Core.Models;

namespace Read2Me.Tests.Fakes;

/// <summary>
/// A real loader that can be made to fail, or held mid-read — the two things a Book View build does
/// that the projection's behaviour hangs on and a real database will not do on cue.
/// </summary>
public sealed class SwitchableLoader(IBookProjectLoader inner) : IBookProjectLoader
{
    public Exception? Failure { get; set; }

    /// <summary>
    /// How many more reads <see cref="Failure"/> applies to. Left as it is, it fails every read
    /// until cleared; set to 1, it fails only the next one — which is how a test tells a targeted
    /// refresh's read apart from the rebuild that falls back to it.
    /// </summary>
    public int FailFor { get; set; } = int.MaxValue;

    /// <summary>How many reads have been failed — the only signal a swallowed failure gives.</summary>
    public int Failures { get; private set; }

    /// <summary>Set to hold the next read until the task completes; cleared once it does.</summary>
    public TaskCompletionSource? Held { get; set; }

    /// <summary>True while a held read is waiting — the only way to tell a build has begun.</summary>
    public bool Reading { get; private set; }

    public async Task<BookProjectSnapshot> LoadSnapshotAsync(
        ProjectFolderId folderId, CancellationToken ct = default)
    {
        if (Held is { } held)
        {
            Held = null;
            Reading = true;
            await held.Task;
            Reading = false;
        }

        if (Failure is { } failure && FailFor > 0)
        {
            Failures++;
            FailFor--;
            throw failure;
        }

        return await inner.LoadSnapshotAsync(folderId, ct);
    }
}
