namespace Read2Me.Services.History;

/// <summary>The words a History step's note is made of, and how they join: "part 2 · 212 of 340 paragraphs (cancelled)".</summary>
internal static class StepNotes
{
    public const string Separator = " · ";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    /// <summary>
    /// How a Background run ended early, and how far it got: "212 of 340 paragraphs (cancelled)",
    /// "3 of 7 voices (failed)".
    /// </summary>
    public static string Ending(int done, int total, string unit, string how) =>
        $"{done} of {(total == 1 ? $"1 {unit}" : $"{total} {unit}s")} ({how})";

    /// <summary>"part 2": a step scope that another commit split carries on in its next part.</summary>
    public static string Part(int part) => $"part {part}";

    /// <summary>The part, when the scope was split, then <paramref name="ending"/>; null when there is neither.</summary>
    public static string? Of(int part, string? ending)
    {
        string?[] pieces = [part > 1 ? Part(part) : null, ending];
        var note = string.Join(Separator, pieces.OfType<string>());
        return note.Length == 0 ? null : note;
    }
}
