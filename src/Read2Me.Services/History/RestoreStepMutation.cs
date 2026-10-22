using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// The <b>Restore</b> Book mutation (spec, "History module"): makes the Project state after
/// <c>Target</c> current. Under the write lock it takes the live → target difference of every
/// state table and applies it, so records keep their ids and the change reads like the equivalent
/// hand edits: items whose text, speaker or delivery cue changes lose their audio and its review,
/// items brought back return without audio, and the Pipeline checkpoints it disturbs go stale.
/// It records the target's state files; they are written from the blobs once it has committed.
/// Only <see cref="HistoryRestore"/> commits one.
/// </summary>
internal sealed record RestoreStepMutation(ProjectFolderId FolderId, HistoryStep Target, RestoreWork Work)
    : ProjectStateMutation(FolderId)
{
    internal override FileChanges? RecordsFileChanges => Work.FileChanges;

    internal override Guid? RestoresStepId => Target.StepId;

    internal override string? StepSummary => RestoreAudio.Summary(Work.AudioDiscardCount);
}

/// <summary>
/// What a Restore makes of the items' audio, on a connection to a copy of the target state with the
/// live project attached as <c>live</c>. Audio is a result, not state, so the target's own
/// <c>AudioFileName</c>s are whatever they were then: an item in both states that kept its text,
/// speaker and delivery cue keeps the audio it has now, and every other one has none.
/// </summary>
internal static class RestoreAudio
{
    /// <summary>A live item <c>l</c> the target item <c>t</c> is unchanged from.</summary>
    private const string Unchanged =
        "t.Id = l.Id AND t.Text IS l.Text AND t.CharacterId IS l.CharacterId AND t.VoiceInstructions IS l.VoiceInstructions";

    /// <summary>Live items with audio that the target doesn't have unchanged: the audio a Restore discards.</summary>
    private const string Discarded = $"""
        FROM live.ParagraphItems l
        WHERE COALESCE(l.AudioFileName, '') <> ''
            AND NOT EXISTS (SELECT 1 FROM main.ParagraphItems t WHERE {Unchanged})
        """;

    public static async Task AttachLiveAsync(SqliteConnection connection, string projectDatabase, CancellationToken ct)
    {
        await using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $path AS live";
        attach.Parameters.AddWithValue("$path", projectDatabase);
        await attach.ExecuteNonQueryAsync(ct);
    }

    public static async Task<int> CountDiscardsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) {Discarded}";
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Gives each target item the audio it keeps, or none, and answers the live items whose audio
    /// is discarded.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> KeepUnchangedAsync(SqliteConnection target, CancellationToken ct)
    {
        var discarded = new List<Guid>();
        await using (var read = target.CreateCommand())
        {
            read.CommandText = $"SELECT l.Id {Discarded}";
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                discarded.Add(Guid.Parse(reader.GetString(0)));
        }

        await using var keep = target.CreateCommand();
        keep.CommandText = $"""
            UPDATE main.ParagraphItems AS t
                SET AudioFileName = (SELECT l.AudioFileName FROM live.ParagraphItems l WHERE {Unchanged})
            """;
        await keep.ExecuteNonQueryAsync(ct);
        return discarded;
    }

    /// <summary>The Restore step's "what changed": the audio it cost, or null when it cost none.</summary>
    public static string? Summary(int discarded) => discarded switch
    {
        0 => null,
        1 => "Discarded audio on 1 item",
        _ => $"Discarded audio on {discarded} items",
    };
}

/// <summary>
/// One Restore's working state: a private copy of the target step's database, which the Restore
/// writes the kept audio into, and the step's state files list. The files themselves are written
/// only after the commit, from the Head's list (<see cref="ProjectHistory.WriteStateFilesAsync"/>).
/// Disposing deletes the copy.
/// </summary>
internal sealed class RestoreWork(string workFile, FileList targetFiles) : IDisposable
{
    public string WorkFile { get; } = workFile;

    public FileList TargetFiles { get; } = targetFiles;

    /// <summary>The items whose audio the Restore discarded, counted under the write lock.</summary>
    public int AudioDiscardCount { get; set; }

    /// <summary>The tip's file list against the target's, taken under the write lock: what the Restore step records.</summary>
    public FileChanges? FileChanges { get; set; }

    public void Dispose() => File.Delete(WorkFile);
}
