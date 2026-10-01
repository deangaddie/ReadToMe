using Read2Me.App.Api;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Api
{
    public class VoiceStepRowTests
    {
        private static VoiceStepRow Row(string stepId) =>
            VoiceStepRow.From(AudioPostProcessStepDefaults.For(StepScope.Voice).Single(c => c.StepId == stepId));

        [Fact]
        public void A_row_is_seeded_unticked_from_the_voice_defaults()
        {
            Assert.False(Row(AudioPostProcessStepIds.Denoise).Ticked);
            Assert.Equal(-35, Row(AudioPostProcessStepIds.SilenceTrim).ThresholdDb);
            Assert.Equal(60, Row(AudioPostProcessStepIds.DePlosive).CutoffHz);
            Assert.Equal(ConsonantSoftenPresets.Light, Row(AudioPostProcessStepIds.ConsonantSoften).Preset);
        }

        [Fact]
        public void A_ticked_row_builds_the_config_its_dials_describe()
        {
            var trim = Row(AudioPostProcessStepIds.SilenceTrim);
            trim.ThresholdDb = -40;

            var settings = trim.BuildConfig().GetSettings<SilenceTrimSettings>()!;

            Assert.Equal(-40, settings.ThresholdDb);
            // The Voice-scope guard rides along — it is a property of (step, scope), not of the dial.
            Assert.Equal(1000, settings.MinOutputMs);
        }
    }
}
