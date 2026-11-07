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
    }
}
