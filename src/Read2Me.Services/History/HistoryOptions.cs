namespace Read2Me.Services.History;

/// <summary>
/// Tuning options for <see cref="ProjectHistory"/>, bound from the <c>History</c> config section.
/// Defaults apply when the section is absent.
/// </summary>
public sealed class HistoryOptions
{
    public const string SectionName = "History";

    /// <summary>
    /// How many closed steps History records between snapshots (ADR 0016): opening a step replays
    /// at most this many steps' chunks onto the nearest earlier snapshot.
    /// </summary>
    public int SnapshotEvery { get; set; } = 50;
}
