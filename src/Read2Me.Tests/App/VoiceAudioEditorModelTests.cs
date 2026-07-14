using Read2Me.App.Shared.Voices;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App
{
    public class VoiceAudioEditorModelTests
    {
        private static readonly VoiceAudioRef Voice =
            new(new ProjectFolderId("book-a"), Guid.NewGuid(), Guid.NewGuid(), "voices/c/v-my-voice.wav");

        private sealed class FakeRenderer : IVoicePreviewRenderer
        {
            public bool Fail { get; set; }
            public int Calls { get; private set; }
            public List<string> LastChain { get; private set; } = [];
            public List<string> LastTokens { get; private set; } = [];
            public string? SkipStepId { get; set; }

            /// <summary>Holds the render open, so the model can be observed mid-flight.</summary>
            public TaskCompletionSource? Gate { get; set; }

            public async Task<VoiceRenderResult> RenderChainAsync(
                VoiceAudioRef voice, IReadOnlyList<AudioPostProcessStepConfig> chain,
                IReadOnlyList<string> tokens, CancellationToken ct = default)
            {
                Calls++;
                LastChain = chain.Select(c => c.StepId).ToList();
                LastTokens = tokens.ToList();

                if (Gate is not null) await Gate.Task;

                if (Fail)
                    return new VoiceRenderResult(null, [], "ffmpeg not found");

                var steps = chain
                    .Select(c => new ChainStepOutcome(
                        c.StepId,
                        Applied: c.StepId != SkipStepId,
                        Reason: c.StepId == SkipStepId ? "ffmpeg not found" : null,
                        Audio: [1]))
                    .ToList();

                return new VoiceRenderResult([0], steps);
            }
        }

        private sealed class FakeEditor : IVoiceAudioEditor
        {
            public List<byte[]> Applied { get; } = [];
            public int Restores { get; private set; }
            public Exception? Throws { get; set; }

            /// <summary>Holds the apply open, so the model can be observed mid-flight.</summary>
            public TaskCompletionSource? Gate { get; set; }

            public async Task ApplyAsync(VoiceAudioRef voice, byte[] processed, CancellationToken ct = default)
            {
                if (Throws is not null) throw Throws;
                if (Gate is not null) await Gate.Task;
                Applied.Add(processed);
            }

            public Task RestoreOriginalAsync(VoiceAudioRef voice, CancellationToken ct = default)
            {
                Restores++;
                return Task.CompletedTask;
            }
        }

        private static VoiceAudioEditorModel NewModel(
            FakeRenderer? renderer = null, FakeEditor? editor = null, bool edited = false) =>
            new(renderer ?? new FakeRenderer(), editor ?? new FakeEditor(), edited);

        private static VoiceStepRow Row(VoiceAudioEditorModel model, string stepId) =>
            model.Rows.Single(r => r.StepId == stepId);

        [Fact]
        public void Starts_with_the_five_steps_unticked_and_seeded_from_the_voice_defaults()
        {
            var model = NewModel();

            Assert.Equal(5, model.Rows.Count);
            Assert.All(model.Rows, r => Assert.False(r.Ticked));
            Assert.Equal(-35, Row(model, AudioPostProcessStepIds.SilenceTrim).ThresholdDb);
            Assert.Equal(60, Row(model, AudioPostProcessStepIds.DePlosive).CutoffHz);
            Assert.Equal(ConsonantSoftenPresets.Light, Row(model, AudioPostProcessStepIds.ConsonantSoften).Preset);
        }

        [Fact]
        public void Cannot_apply_with_no_steps_ticked()
        {
            var model = NewModel();

            Assert.False(model.CanApply);
            Assert.Equal("Tick at least one step", model.ApplyBlockedReason);
        }

        [Fact]
        public void Ticking_a_step_leaves_the_render_stale()
        {
            var model = NewModel();

            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);

            Assert.True(model.Stale);
            Assert.False(model.CanApply);
            Assert.Equal("Preview first", model.ApplyBlockedReason);
        }

        [Fact]
        public async Task A_preview_clears_stale_and_enables_apply()
        {
            var model = NewModel();
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);

            await model.PreviewAsync(Voice);

            Assert.False(model.Stale);
            Assert.True(model.CanApply);
            Assert.Null(model.ApplyBlockedReason);
        }

        [Fact]
        public async Task Apply_is_blocked_with_a_reason_while_the_preview_renders()
        {
            // CanApply is false mid-render, so the tooltip must say why — never a disabled button
            // under an empty tooltip.
            var renderer = new FakeRenderer { Gate = new TaskCompletionSource() };
            var model = NewModel(renderer);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);

            var preview = model.PreviewAsync(Voice);

            Assert.True(model.Rendering);
            Assert.False(model.CanApply);
            Assert.NotNull(model.ApplyBlockedReason);

            renderer.Gate.SetResult();
            await preview;
        }

        [Fact]
        public async Task Apply_is_blocked_with_a_reason_while_it_is_applying()
        {
            var editor = new FakeEditor { Gate = new TaskCompletionSource() };
            var model = NewModel(editor: editor);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);
            await model.PreviewAsync(Voice);

            var apply = model.ApplyAsync(Voice);

            Assert.True(model.Applying);
            Assert.False(model.CanApply);
            Assert.NotNull(model.ApplyBlockedReason);

            editor.Gate.SetResult();
            Assert.True(await apply);
        }

        [Fact]
        public async Task A_dial_edit_after_a_preview_stales_it_again()
        {
            // Otherwise Apply would write bytes the user never heard.
            var model = NewModel();
            var denoise = Row(model, AudioPostProcessStepIds.Denoise);
            model.SetTicked(denoise, true);
            await model.PreviewAsync(Voice);

            model.EditDial(() => denoise.Strength = 500);

            Assert.True(model.Stale);
            Assert.False(model.CanApply);
        }

        [Fact]
        public async Task Untick_after_a_preview_stales_it_again()
        {
            var model = NewModel();
            var denoise = Row(model, AudioPostProcessStepIds.Denoise);
            model.SetTicked(denoise, true);
            await model.PreviewAsync(Voice);

            model.SetTicked(denoise, false);

            Assert.True(model.Stale);
            Assert.Equal("Tick at least one step", model.ApplyBlockedReason);
        }

        [Fact]
        public async Task The_chain_is_the_ticked_steps_in_the_fixed_order()
        {
            var model = NewModel();
            var renderer = new FakeRenderer();
            model = NewModel(renderer);

            // Ticked bottom-up; the chain must still come out in the fixed order.
            model.SetTicked(Row(model, AudioPostProcessStepIds.SilenceTrim), true);
            model.SetTicked(Row(model, AudioPostProcessStepIds.DePlosive), true);
            await model.PreviewAsync(Voice);

            Assert.Equal(
                [AudioPostProcessStepIds.DePlosive, AudioPostProcessStepIds.SilenceTrim],
                renderer.LastChain);
        }

        [Fact]
        public async Task Preview_tokens_are_minted_once_per_page_not_per_render()
        {
            var renderer = new FakeRenderer();
            var model = NewModel(renderer);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);

            await model.PreviewAsync(Voice);
            var first = renderer.LastTokens;
            await model.PreviewAsync(Voice);

            Assert.Equal(first, renderer.LastTokens);
            Assert.Equal(2, renderer.Calls);
        }

        [Fact]
        public async Task A_failed_render_leaves_stale_true_so_apply_cannot_write()
        {
            var editor = new FakeEditor();
            var model = NewModel(new FakeRenderer { Fail = true }, editor);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);

            await model.PreviewAsync(Voice);

            Assert.True(model.Stale);
            Assert.False(model.CanApply);
            Assert.NotNull(model.Error);
            Assert.False(await model.ApplyAsync(Voice));
            Assert.Empty(editor.Applied);
        }

        [Fact]
        public async Task A_skipped_step_reports_its_reason_on_its_own_row_not_the_page()
        {
            var model = NewModel(new FakeRenderer { SkipStepId = AudioPostProcessStepIds.Denoise });
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);

            await model.PreviewAsync(Voice);

            Assert.Equal("ffmpeg not found", Row(model, AudioPostProcessStepIds.Denoise).SkipReason);
            Assert.Null(model.Error);
            // A skip is not a failed render — the player still holds honest audio.
            Assert.False(model.Stale);
        }

        [Fact]
        public async Task Apply_writes_the_last_render_s_final_bytes_and_lights_the_edited_flag()
        {
            var editor = new FakeEditor();
            var model = NewModel(new FakeRenderer(), editor);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);
            await model.PreviewAsync(Voice);

            Assert.True(await model.ApplyAsync(Voice));

            Assert.Equal([1], Assert.Single(editor.Applied));
            Assert.True(model.Edited);
        }

        [Fact]
        public async Task The_page_survives_apply_so_a_second_apply_is_idempotent()
        {
            // The input is still the same original, so the ticks, dials and players stay truthful.
            var editor = new FakeEditor();
            var model = NewModel(new FakeRenderer(), editor);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);
            await model.PreviewAsync(Voice);

            await model.ApplyAsync(Voice);
            await model.ApplyAsync(Voice);

            Assert.Equal(2, editor.Applied.Count);
            Assert.Equal(editor.Applied[0], editor.Applied[1]);
            Assert.True(model.CanApply);
        }

        [Fact]
        public async Task A_failed_apply_leaves_the_edited_flag_alone()
        {
            var editor = new FakeEditor { Throws = new IOException("disk full") };
            var model = NewModel(new FakeRenderer(), editor);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);
            await model.PreviewAsync(Voice);

            Assert.False(await model.ApplyAsync(Voice));

            Assert.False(model.Edited);
            Assert.Equal("disk full", model.Error);
        }

        [Fact]
        public async Task Restore_clears_the_ticks_the_previews_and_the_edited_flag()
        {
            var editor = new FakeEditor();
            var model = NewModel(new FakeRenderer(), editor, edited: true);
            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);
            await model.PreviewAsync(Voice);

            Assert.True(await model.RestoreAsync(Voice));

            Assert.Equal(1, editor.Restores);
            Assert.False(model.Edited);
            Assert.False(model.AnyTicked);
            Assert.True(model.Stale);
            Assert.All(model.Rows, r => Assert.Null(model.PreviewUrl(r)));
        }

        [Fact]
        public async Task Restore_on_an_unedited_voice_does_nothing()
        {
            var editor = new FakeEditor();
            var model = NewModel(new FakeRenderer(), editor);

            Assert.False(await model.RestoreAsync(Voice));
            Assert.Equal(0, editor.Restores);
        }

        [Fact]
        public void The_hiss_hint_fires_only_when_denoise_and_hiss_reduce_are_both_ticked()
        {
            var model = NewModel();

            model.SetTicked(Row(model, AudioPostProcessStepIds.HissReduce), true);
            Assert.False(model.ShowHissRedundantHint);

            model.SetTicked(Row(model, AudioPostProcessStepIds.Denoise), true);
            Assert.True(model.ShowHissRedundantHint);

            // A hint, never a skip — the step still runs.
            model.SetTicked(Row(model, AudioPostProcessStepIds.HissReduce), false);
            Assert.False(model.ShowHissRedundantHint);
        }

        [Fact]
        public async Task The_player_url_is_cache_busted_per_render()
        {
            var model = NewModel();
            var denoise = Row(model, AudioPostProcessStepIds.Denoise);
            Assert.Null(model.PreviewUrl(denoise));

            model.SetTicked(denoise, true);
            await model.PreviewAsync(Voice);
            var first = model.PreviewUrl(denoise);

            await model.PreviewAsync(Voice);

            Assert.NotNull(first);
            Assert.NotEqual(first, model.PreviewUrl(denoise));
        }

        [Fact]
        public void Selection_and_ticking_are_independent()
        {
            var model = NewModel();
            var hiss = Row(model, AudioPostProcessStepIds.HissReduce);

            model.Select(hiss);

            Assert.Same(hiss, model.Selected);
            Assert.False(hiss.Ticked);
        }

        [Fact]
        public void A_ticked_row_builds_the_config_its_dials_describe()
        {
            var model = NewModel();
            var trim = Row(model, AudioPostProcessStepIds.SilenceTrim);
            model.EditDial(() => trim.ThresholdDb = -40);

            var settings = trim.BuildConfig().GetSettings<SilenceTrimSettings>()!;

            Assert.Equal(-40, settings.ThresholdDb);
            // The Voice-scope guard rides along — it is a property of (step, scope), not of the dial.
            Assert.Equal(1000, settings.MinOutputMs);
        }
    }
}
