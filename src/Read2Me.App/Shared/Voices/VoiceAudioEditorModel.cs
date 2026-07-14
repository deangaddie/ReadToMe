using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Audio;

namespace Read2Me.App.Shared.Voices
{
    /// <summary>
    /// Edit-state for the voice audio editor page: which of the five fixed-order steps are ticked, the
    /// dials for each, and the per-step preview players. The razor is thin binding over this — the
    /// <see cref="ConsonantSoftenForm"/> pattern — so <see cref="Stale"/>, <see cref="CanApply"/> and
    /// the hiss hint are plain, testable state transitions.
    /// <para>
    /// One-shot by design: nothing here is persisted. The page is rebuilt from the voice plus the
    /// Voice-scope code defaults on every visit, and every render starts from the voice's <b>original</b>
    /// audio — so a second edit never stacks filters on filters, and a second Apply is idempotent.
    /// </para>
    /// </summary>
    public sealed class VoiceAudioEditorModel
    {
        private readonly IVoicePreviewRenderer _renderer;
        private readonly IVoiceAudioEditor _editor;

        /// One token per step per <b>page instance</b>, never per render. AudioPreviewStore's
        /// token→path dictionary does not evict, and a user tuning dials presses Preview repeatedly —
        /// minting per render would grow it without bound. Five tokens, overwritten in place, and the
        /// player URL is cache-busted with the render count instead.
        private readonly string _pageId = Guid.NewGuid().ToString("N");

        private byte[]? _final;

        public VoiceAudioEditorModel(IVoicePreviewRenderer renderer, IVoiceAudioEditor editor, bool edited = false)
        {
            _renderer = renderer;
            _editor = editor;
            Edited = edited;
            Rows = AudioPostProcessStepDefaults.For(StepScope.Voice)
                .Select(VoiceStepRow.From)
                .ToList();
            Selected = Rows[0];
        }

        public IReadOnlyList<VoiceStepRow> Rows { get; }
        public VoiceStepRow Selected { get; private set; }

        /// <summary>True until a render lands, and again after any tick, untick or dial edit.</summary>
        public bool Stale { get; private set; } = true;

        public bool Rendering { get; private set; }
        public bool Applying { get; private set; }

        /// <summary>Page-level failure (ffmpeg missing, render or write threw). Per-step skips are on the rows.</summary>
        public string? Error { get; private set; }

        /// <summary>Cache-buster: the preview files are overwritten in place under stable tokens.</summary>
        public int RenderCount { get; private set; }

        /// <summary>The invariant, mirrored: <c>{voiceId}.orig.wav</c> exists ⟺ this voice has been edited.</summary>
        public bool Edited { get; private set; }

        public IEnumerable<VoiceStepRow> Ticked => Rows.Where(r => r.Ticked);
        public bool AnyTicked => Rows.Any(r => r.Ticked);

        public bool CanApply => !Stale && AnyTicked && !Rendering && !Applying;

        /// <summary>
        /// Why Apply is disabled, for the tooltip. Null when it is enabled — and non-null whenever
        /// <see cref="CanApply"/> is false, so the button never sits disabled under an empty tooltip.
        /// </summary>
        public string? ApplyBlockedReason =>
            Rendering ? "Rendering the preview…"
            : Applying ? "Applying…"
            : !AnyTicked ? "Tick at least one step"
            : Stale ? "Preview first"
            : null;

        /// <summary>
        /// Denoise removes hiss broadband and more thoroughly than hiss-reduce does, so ticking both
        /// makes the second a near no-op. A hint, not a skip and not a mutual exclusion — the checklist
        /// stays flat, the step still runs, and the cumulative players let the user hear it do nothing.
        /// </summary>
        public bool ShowHissRedundantHint =>
            IsTicked(AudioPostProcessStepIds.Denoise) && IsTicked(AudioPostProcessStepIds.HissReduce);

        public void Select(VoiceStepRow row) => Selected = row;

        public void SetTicked(VoiceStepRow row, bool ticked)
        {
            row.Ticked = ticked;
            MarkStale();
        }

        /// <summary>Applies a dial edit. Every dial goes through here so none can forget to stale the render.</summary>
        public void EditDial(Action edit)
        {
            edit();
            MarkStale();
        }

        public async Task PreviewAsync(VoiceAudioRef voice, CancellationToken ct = default)
        {
            if (Rendering || !AnyTicked) return;

            Rendering = true;
            Error = null;
            try
            {
                var chain = Ticked.Select(r => r.BuildConfig()).ToList();
                var tokens = Ticked.Select(r => r.Token(_pageId)).ToList();

                var result = await _renderer.RenderChainAsync(voice, chain, tokens, ct);

                if (result.Source is null || result.Steps.Count == 0)
                {
                    // Nothing was parked, so the players still hold the previous render. Stale stays
                    // true, which is what stops Apply writing bytes the user never heard.
                    Error = result.Error ?? "the preview could not be rendered";
                    return;
                }

                foreach (var (row, outcome) in Ticked.Zip(result.Steps))
                    row.RecordOutcome(outcome);

                _final = result.Final;
                RenderCount++;
                Stale = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = ex.Message;
            }
            finally
            {
                Rendering = false;
            }
        }

        /// <summary>
        /// Writes the last render's final bytes over the voice's live WAV. The ticks, dials and players
        /// survive: the input is still the same original, so they stay truthful and a second Apply is
        /// idempotent.
        /// </summary>
        public async Task<bool> ApplyAsync(VoiceAudioRef voice, CancellationToken ct = default)
        {
            if (!CanApply || _final is null) return false;

            Applying = true;
            Error = null;
            try
            {
                await _editor.ApplyAsync(voice, _final, ct);
                Edited = true;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = ex.Message;
                return false;
            }
            finally
            {
                Applying = false;
            }
        }

        /// <summary>Restores the original, then clears the page: the previews described audio that is gone.</summary>
        public async Task<bool> RestoreAsync(VoiceAudioRef voice, CancellationToken ct = default)
        {
            if (!Edited || Applying) return false;

            Applying = true;
            Error = null;
            try
            {
                await _editor.RestoreOriginalAsync(voice, ct);
                Edited = false;
                foreach (var row in Rows) row.Reset();
                _final = null;
                RenderCount = 0;
                Stale = true;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = ex.Message;
                return false;
            }
            finally
            {
                Applying = false;
            }
        }

        /// <summary>The player for a ticked step's output, or null before the first render.</summary>
        public string? PreviewUrl(VoiceStepRow row) =>
            RenderCount == 0 || !row.HasPreview ? null : $"/audio-preview/{row.Token(_pageId)}?v={RenderCount}";

        private bool IsTicked(string stepId) => Rows.Any(r => r.StepId == stepId && r.Ticked);

        private void MarkStale()
        {
            Stale = true;
            Error = null;
        }
    }
}
