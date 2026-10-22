using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services.History;

/// <summary>
/// The Project state after one History step, materialised in the project's <c>history/cache/</c>
/// and opened <c>Mode=ReadOnly</c>: what a project read at <c>?at=</c> reads instead of
/// <c>project.db</c>. A <see cref="ProjectDbSession"/> holding it as a folder's state reference
/// opens it in place of the live database (spec, "Read side: a state reference").
/// </summary>
public sealed class StepState
{
    private readonly string _databaseFile;

    internal StepState(HistoryStep step, string databaseFile)
    {
        Step = step;
        _databaseFile = databaseFile;
    }

    public HistoryStep Step { get; }

    /// <summary>The materialised copy in the cache: read it, never write it.</summary>
    internal string DatabaseFile => _databaseFile;

    /// <summary>A read-only context over the materialised step: reads only, never written. Caller owns dispose.</summary>
    public ProjectDbContext OpenContext() => new(new DbContextOptionsBuilder<ProjectDbContext>()
        .UseSqlite($"Data Source={_databaseFile};Mode=ReadOnly;Pooling=false")
        .Options);
}

/// <summary>What opening a History step answered: its state, no such step, or a step that can't be opened.</summary>
public abstract record StepOpening
{
    private StepOpening() { }

    public sealed record Opened(StepState State) : StepOpening;

    /// <summary>The project's History has no step with that id (or the project has no History yet).</summary>
    public sealed record UnknownStep : StepOpening;

    /// <summary>The step is marked can't-be-opened: its chunks failed to replay, or its old schema to migrate.</summary>
    public sealed record CantOpen(HistoryStep Step) : StepOpening;

    /// <summary>Opening failed for a reason that passes (an IO error, a full disk, a busy database); nothing is marked.</summary>
    public sealed record TryAgain : StepOpening;
}
