using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.History;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Applies a <see cref="RestoreStepMutation"/> under the write lock: the live → target difference of
/// every state table, the audio and reviews it discards, the checkpoints it disturbs, and the state
/// file changes it records. The files themselves are written after the commit.
/// </summary>
internal sealed class RestoreStepMutationImplementation : IBookMutationImplementation<RestoreStepMutation>
{
    private const string Live = "live";

    private static readonly PipelineStep[] ChapterLevel = [PipelineStep.Structure, PipelineStep.Titles, PipelineStep.Pauses];

    public async Task<BookMutationEffects> ApplyAsync(RestoreStepMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var projectFolder = Path.GetDirectoryName(connection.DataSource)!;
        // Under the write lock, so it is the list the capture records against.
        var tipFiles = await ProjectHistory.TipFilesAsync(HistoryDatabase.FolderOf(projectFolder), ct);

        // The target copy is main and the live project is attached, read-only in effect, so the diff
        // reads live as committed: under the write lock, no one else can move it.
        byte[] patchset;
        IReadOnlyList<Guid> discarded;
        IReadOnlyList<PipelineStep> disturbed;
        await using (var target = HistoryDatabase.Open(mutation.Work.WorkFile, SqliteOpenMode.ReadWrite))
        {
            await target.OpenAsync(ct);
            await RestoreAudio.AttachLiveAsync(target, connection.DataSource, ct);
            discarded = await RestoreAudio.KeepUnchangedAsync(target, ct);
            mutation.Work.NeedsAudioCount = await RestoreAudio.CountWithoutAudioAsync(target, ct);
            disturbed = await DisturbedStepsAsync(target, ct);
            using var session = SqliteSession.Create(target);
            foreach (var table in ChildrenFirst(db))
            {
                session.Attach(table);
                session.Diff(Live, table);
            }
            patchset = session.Patchset();
        }

        if (patchset.Length > 0)
            SqliteSession.ApplyOverCascades(connection, patchset);
        if (discarded.Count > 0)
            await db.AudioReviews.Where(r => discarded.Contains(r.ParagraphItemId)).ExecuteDeleteAsync(ct);
        if (disturbed.Count > 0)
            await db.StepCheckpoints.Where(c => disturbed.Contains(c.Step) && !c.Stale)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Stale, true), ct);
        mutation.Work.FileChanges = FileChanges.Between(tipFiles, mutation.Work.TargetFiles);
        mutation.Work.AudioDiscardCount = discarded.Count;

        return patchset.Length == 0 && mutation.Work.FileChanges.Count == 0 ? BookMutationEffects.Nothing : BookMutationEffects.Unknown;
    }

    /// <summary>
    /// The project's state tables, each one before any it refers to, so a row is removed before the
    /// row it needs — a Voice Rule before its Voice, which restricts the delete — and cascades have
    /// less to do. The diff and the apply follow this order.
    /// </summary>
    private static List<string> ChildrenFirst(ProjectDbContext db)
    {
        var notState = ProjectHistory.NotStateTables(db);
        var remaining = db.Model.GetEntityTypes()
            .Where(t => t.GetTableName() is { } name && !notState.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var ordered = new List<IEntityType>();
        while (remaining.Count > 0)
        {
            // A table no remaining table refers to; in a cycle, any one.
            var next = remaining.FirstOrDefault(t => !remaining.Any(d => d != t && d.GetForeignKeys().Any(fk => fk.PrincipalEntityType == t)))
                ?? remaining[0];
            ordered.Add(next);
            remaining.Remove(next);
        }
        return [.. ordered.Select(t => t.GetTableName()!)];
    }

    /// <summary>
    /// The Pipeline steps the Restore disturbs, read before it applies, as the equivalent edits would
    /// stale them (<see cref="Checkpoints.CheckpointStaleness"/>): a changed Volume, Part or Chapter —
    /// a split, merge, delete or retitle — unsettles structure, titles and pauses, and changed item
    /// text, as an approved edit program does, the text. Paragraph inserts, Voices and Speakers are
    /// never staled, as no edit stales them.
    /// </summary>
    private static async Task<IReadOnlyList<PipelineStep>> DisturbedStepsAsync(SqliteConnection target, CancellationToken ct)
    {
        var steps = new List<PipelineStep>();
        if (await DiffersAsync(target, "Volumes", "*", ct) || await DiffersAsync(target, "Parts", "*", ct)
            || await DiffersAsync(target, "Chapters", "*", ct))
            steps.AddRange(ChapterLevel);
        if (await DiffersAsync(target, "ParagraphItems", "Id, Text", ct))
            steps.Add(PipelineStep.Text);
        return steps;
    }

    private static async Task<bool> DiffersAsync(SqliteConnection target, string table, string columns, CancellationToken ct)
    {
        await using var command = target.CreateCommand();
        command.CommandText = $"""
            SELECT EXISTS (SELECT {columns} FROM main."{table}" EXCEPT SELECT {columns} FROM {Live}."{table}")
                OR EXISTS (SELECT {columns} FROM {Live}."{table}" EXCEPT SELECT {columns} FROM main."{table}")
            """;
        return (long)(await command.ExecuteScalarAsync(ct))! != 0;
    }
}

