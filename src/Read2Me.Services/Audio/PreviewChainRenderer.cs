namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The source-agnostic core of every A/B preview: fold a chain over some source bytes and park
    /// <b>each step's</b> output in <see cref="AudioPreviewStore"/> under the token the caller minted
    /// for that step. It knows nothing about where the audio came from — the paragraph card reads a
    /// Preview Source, the voice editor reads the voice's original — which is what lets one renderer
    /// serve both.
    /// </summary>
    public interface IPreviewChainRenderer
    {
        /// <summary>
        /// Runs <paramref name="chain"/> over <paramref name="source"/> and stores step <c>i</c>'s output
        /// under <c>tokens[i]</c>. A skipped step still parks a token — its input, unchanged — so the
        /// players line up one-to-one with the ticked steps and the user hears the step do nothing.
        /// </summary>
        Task<ChainResult> RenderAsync(
            byte[] source, IReadOnlyList<AudioPostProcessStepConfig> chain, IReadOnlyList<string> tokens,
            string? ffmpegPath, CancellationToken ct = default);
    }

    public sealed class PreviewChainRenderer(
        IAudioPostProcessChain chain,
        AudioPreviewStore store) : IPreviewChainRenderer
    {
        public async Task<ChainResult> RenderAsync(
            byte[] source, IReadOnlyList<AudioPostProcessStepConfig> configs, IReadOnlyList<string> tokens,
            string? ffmpegPath, CancellationToken ct = default)
        {
            var result = await chain.RunAsync(source, configs, ffmpegPath, ct);

            for (var i = 0; i < result.Steps.Count && i < tokens.Count; i++)
                await store.SaveAsync(tokens[i], result.Steps[i].Audio, ct);

            return result;
        }
    }
}
