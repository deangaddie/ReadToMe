using Read2Me.AppData.Entities;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// The one <see cref="IChainStep"/> the walk sees: routes a rung by its effective prompt style.
    /// <see cref="AttributionPromptStyle.Chapter"/> goes to the chapter pass; every other style goes
    /// to the existing step with the walk's options untouched, which is what keeps the
    /// <c>Full</c>/<c>Simple</c> path byte-for-byte as it was before the chapter pass existed.
    /// </summary>
    /// <param name="existing">The existing step (<see cref="CharacterAttributionService"/>).</param>
    /// <param name="chapter">The chapter pass (<see cref="ChapterPass.ChapterAttributionStep"/>).</param>
    internal sealed class StyleRoutedChainStep(IChainStep existing, IChainStep chapter) : IChainStep
    {
        /// <inheritdoc/>
        public IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunAsync(
            IReadOnlyList<QueuedParagraph> items,
            ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks,
            CancellationToken ct) =>
            opts.EffectiveStyle == AttributionPromptStyle.Chapter
                ? chapter.RunAsync(items, opts, callbacks, ct)
                : existing.RunAsync(items, opts, callbacks, ct);
    }
}
