using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// What one step did in a chain run. <see cref="Audio"/> is the step's <b>output</b> — for a
    /// skipped step that is its input, unchanged (already the <see cref="PostProcessResult"/>
    /// contract), so a caller can play "the audio as of this step" without a special case. Handing
    /// the intermediates back is what makes the voice editor's per-step cumulative players free:
    /// every step is already its own ffmpeg pass.
    /// </summary>
    public sealed record ChainStepOutcome(string StepId, bool Applied, string? Reason, byte[] Audio);

    /// <summary><see cref="Audio"/> is the last step's output, or the input when the chain is empty.</summary>
    public sealed record ChainResult(byte[] Audio, IReadOnlyList<ChainStepOutcome> Steps);

    /// <summary>
    /// Folds a list of post-process steps over a WAV buffer, carrying the bytes forward. The one fold
    /// in the codebase: the paragraph pipeline runs the app-settings chain through it, the voice
    /// editor runs the user's one-shot selection. Never throws — the steps already honour the
    /// never-throw / never-lose-audio contract, and an unregistered id is skipped like a failed step.
    /// </summary>
    public interface IAudioPostProcessChain
    {
        Task<ChainResult> RunAsync(byte[] wav, IReadOnlyList<AudioPostProcessStepConfig> chain,
                                   string? ffmpegPath, CancellationToken ct);
    }

    public sealed class AudioPostProcessChain(
        IEnumerable<IAudioPostProcessStep> steps,
        ILogger<AudioPostProcessChain> logger) : IAudioPostProcessChain
    {
        public Task<ChainResult> RunAsync(
            byte[] wav, IReadOnlyList<AudioPostProcessStepConfig> chain, string? ffmpegPath, CancellationToken ct)
        {
            var byId = steps.GroupBy(s => s.StepId).ToDictionary(g => g.Key, g => g.First());

            var resolved = chain
                .Where(c => byId.ContainsKey(c.StepId))
                .Select(c => new ResolvedStep(byId[c.StepId], c.SettingsJson))
                .ToList();

            foreach (var unregistered in chain.Where(c => !byId.ContainsKey(c.StepId)))
                logger.LogWarning("post-process step '{Step}' is not registered; skipping", unregistered.StepId);

            return FoldAsync(wav, resolved, ffmpegPath, logger, ct);
        }

        /// <summary>
        /// The fold itself, over steps that are <i>already</i> resolved. Callers that hold step
        /// instances (the paragraph pipeline, whose catalog resolves them) use this directly rather
        /// than round-tripping ids through the registry.
        /// </summary>
        public static async Task<ChainResult> FoldAsync(
            byte[] wav, IReadOnlyList<ResolvedStep> chain, string? ffmpegPath, ILogger logger, CancellationToken ct)
        {
            var audio = wav;
            var outcomes = new List<ChainStepOutcome>(chain.Count);

            foreach (var (step, settingsJson) in chain)
            {
                ct.ThrowIfCancellationRequested();

                var beforeBytes = audio.Length;
                var sw = Stopwatch.StartNew();
                var result = await step.ProcessAsync(audio, ffmpegPath, settingsJson, ct);
                sw.Stop();

                audio = result.Audio;
                outcomes.Add(new ChainStepOutcome(step.StepId, result.Applied, result.Reason, audio));

                if (!result.Applied)
                {
                    logger.LogWarning(
                        "post-process step '{Step}' skipped: {Reason}", step.StepId, result.Reason);
                }
                else
                {
                    logger.LogDebug(
                        "post-process step '{Step}' applied in {Ms} ms — {Before} -> {After} bytes " +
                        "({RemovedMs:0}ms removed)",
                        step.StepId, sw.ElapsedMilliseconds, beforeBytes, audio.Length,
                        CanonicalWav.RemovedMs(beforeBytes, audio.Length));
                }
            }

            return new ChainResult(audio, outcomes);
        }
    }

    /// <summary>A step paired with the settings payload to run it with.</summary>
    public readonly record struct ResolvedStep(IAudioPostProcessStep Step, string? SettingsJson);
}
