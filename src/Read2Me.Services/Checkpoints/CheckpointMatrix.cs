using Read2Me.Data.Enums;

namespace Read2Me.Services.Checkpoints;

/// <summary>
/// Which checkpoint states each Pipeline step takes. The host owns this table and the web's
/// checkpoint pills mirror it. A step with no states is done by its data alone, so it has nothing
/// to confirm, skip or reopen.
/// </summary>
public static class CheckpointMatrix
{
    private static readonly CheckpointState[] ConfirmOrSkip = [CheckpointState.Confirmed, CheckpointState.Skipped];

    public static IReadOnlyList<CheckpointState> AllowedStates(PipelineStep step) => step switch
    {
        PipelineStep.Structure or PipelineStep.Text or PipelineStep.Titles => ConfirmOrSkip,
        PipelineStep.Speakers => [CheckpointState.Confirmed],
        PipelineStep.Pauses => [CheckpointState.Skipped],
        _ => [],
    };

    /// <summary>
    /// The sentence that refuses <paramref name="state"/> on <paramref name="step"/>, or null when the
    /// matrix allows it. A null state is "open": allowed wherever some state could be set.
    /// </summary>
    public static string? Refusal(PipelineStep step, CheckpointState? state)
    {
        var allowed = AllowedStates(step);
        if (allowed.Count == 0)
            return $"{step} is done by its data, not confirmed or skipped.";
        if (state is not { } s || allowed.Contains(s))
            return null;
        return allowed[0] == CheckpointState.Confirmed
            ? $"{step} can only be confirmed."
            : $"{step} can only be skipped.";
    }
}
