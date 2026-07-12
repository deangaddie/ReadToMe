using Read2Me.App.Shared;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App
{
    public class SilenceTrimFormTests
    {
        [Fact]
        public void FromConfig_Null_IsTheEnabledDefault()
        {
            var form = SilenceTrimForm.FromConfig(null);

            Assert.True(form.Enabled);
            Assert.Equal(-50, form.ThresholdDb);
            Assert.Equal(50, form.PadMs);
        }

        [Fact]
        public void BuildConfig_CarriesTheSilenceTrimStepId()
        {
            var config = SilenceTrimForm.FromConfig(null).BuildConfig();

            Assert.Equal(AudioPostProcessStepIds.SilenceTrim, config.StepId);
        }

        [Fact]
        public void SaveLoad_RoundTrips()
        {
            var form = SilenceTrimForm.FromConfig(null);
            form.Enabled = false;
            form.ThresholdDb = -38;
            form.PadMs = 120;

            var reloaded = SilenceTrimForm.FromConfig(form.BuildConfig());

            Assert.False(reloaded.Enabled);
            Assert.Equal(-38, reloaded.ThresholdDb);
            Assert.Equal(120, reloaded.PadMs);
        }

        [Fact]
        public void BuildConfig_NegativePad_ClampsToZero()
        {
            var form = SilenceTrimForm.FromConfig(null);
            form.PadMs = -10;

            var settings = form.BuildConfig().GetSettings<SilenceTrimSettings>();

            Assert.Equal(0, settings!.PadMs);
        }

        [Fact]
        public void FromConfig_DisabledStoredStep_StaysDisabled()
        {
            var config = AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.SilenceTrim, enabled: false, new SilenceTrimSettings());

            var form = SilenceTrimForm.FromConfig(config);

            Assert.False(form.Enabled);
        }
    }
}
