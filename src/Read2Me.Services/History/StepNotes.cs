namespace Read2Me.Services.History;

/// <summary>
/// The words a History step's note is made of, and how they join: "part 2 · 212 of 340 paragraphs,
/// cancelled". Notes are stored without brackets; a client shows one as "Label (note)".
/// </summary>
internal static class StepNotes
{
    public const string Separator = " · ";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    /// <summary>How a Background run that failed outright ended, told apart from the items that failed on their own.</summary>
    public const string StoppedOnError = "stopped on an error";

    /// <summary>
    /// How a Background run's kind ended, or null when it drained with nothing failed. Ended early
    /// (<paramref name="how"/> set), it says how far it got: "212 of 340 paragraphs, cancelled",
    /// "3 of 7 voices, stopped on an error". Items that failed on their own add their count: "3
    /// paragraphs failed" for a run that drained, "212 of 340 paragraphs, cancelled · 3 failed" for
    /// one that did not.
    /// </summary>
    public static string? Ending(int done, int total, string unit, string? how, int failed = 0)
    {
        if (how is null)
            return failed > 0 ? $"{Count(failed, unit)} {Failed}" : null;
        var ending = $"{done} of {Count(total, unit)}, {how}";
        return failed > 0 ? $"{ending}{Separator}{failed} {Failed}" : ending;
    }

    /// <summary>"part 2": a step scope that another commit split carries on in its next part.</summary>
    public static string Part(int part) => $"part {part}";

    /// <summary>The part, when the scope was split, then <paramref name="ending"/>; null when there is neither.</summary>
    public static string? Of(int part, string? ending)
    {
        string?[] pieces = [part > 1 ? Part(part) : null, ending];
        var note = string.Join(Separator, pieces.OfType<string>());
        return note.Length == 0 ? null : note;
    }

    private static string Count(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
