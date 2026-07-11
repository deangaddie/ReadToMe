using Read2Me.App.Shared;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ConsonantSoftenFormTests
    {
        private static ConsonantSoftenSettings SettingsOf(ConsonantSoftenForm form) =>
            form.BuildConfig().GetSettings<ConsonantSoftenSettings>()!;

        [Fact]
        public void FromConfig_NullConfig_IsDisabledWithAdynEqStrong()
        {
            var form = ConsonantSoftenForm.FromConfig(null);

            Assert.False(form.Enabled);
            Assert.Equal(ConsonantSoftenEngines.AdynEq, form.Engine);
            Assert.Equal(ConsonantSoftenPresets.Strong, form.Preset);
        }

        [Fact]
        public void BuildConfig_RoundTripsEnabledEngineAndPreset()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.Enabled = true;
            form.SetEngine(ConsonantSoftenEngines.Deesser);
            form.SetPreset(ConsonantSoftenPresets.Light);

            var reloaded = ConsonantSoftenForm.FromConfig(form.BuildConfig());

            Assert.True(reloaded.Enabled);
            Assert.Equal(ConsonantSoftenEngines.Deesser, reloaded.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, reloaded.Preset);
        }

        [Fact]
        public void BuildConfig_NonCustomPreset_OmitsRawParams()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Medium);
            form.AdynEq.ThresholdDb = -12; // tweaked, but preset mode wins

            var settings = SettingsOf(form);

            Assert.Null(settings.AdynEq);
            Assert.Null(settings.Deesser);
            Assert.Equal(ConsonantSoftenPresets.Medium, settings.Preset);
        }

        [Fact]
        public void SetPreset_Custom_SeedsDraftsFromSelectedPreset()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Light);
            form.SetPreset(ConsonantSoftenPresets.Custom);

            var light = ConsonantSoftenPresets.ResolveAdynEq(ConsonantSoftenPresets.Light);
            Assert.Equal(light.ThresholdDb, form.AdynEq.ThresholdDb);
            Assert.Equal(light.Ratio, form.AdynEq.Ratio);
            Assert.Equal(light.RangeDb, form.AdynEq.RangeDb);

            var lightDeesser = ConsonantSoftenPresets.ResolveDeesser(ConsonantSoftenPresets.Light);
            Assert.Equal(lightDeesser.Intensity, form.Deesser.Intensity);
        }

        [Fact]
        public void SetPreset_BackToPreset_DiscardsCustomTweaks()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Custom);
            form.AdynEq.ThresholdDb = -12;

            form.SetPreset(ConsonantSoftenPresets.Strong);
            form.SetPreset(ConsonantSoftenPresets.Custom);

            var strong = ConsonantSoftenPresets.ResolveAdynEq(ConsonantSoftenPresets.Strong);
            Assert.Equal(strong.ThresholdDb, form.AdynEq.ThresholdDb);
        }

        [Fact]
        public void SetPreset_AlsoResetsHighpass()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Custom);
            form.HighpassEnabled = true;
            form.HighpassHz = 120;

            form.SetPreset(ConsonantSoftenPresets.Strong);

            Assert.False(form.HighpassEnabled);
            Assert.Equal(ConsonantSoftenForm.DefaultHighpassHz, form.HighpassHz);
        }

        [Fact]
        public void BuildConfig_Custom_PersistsRawParamsForBothEngines()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.Enabled = true;
            form.SetPreset(ConsonantSoftenPresets.Custom);
            form.AdynEq.ThresholdDb = -12;
            form.AdynEq.Ratio = 3;
            form.Deesser.Intensity = 0.42;

            var reloaded = ConsonantSoftenForm.FromConfig(form.BuildConfig());

            Assert.Equal(ConsonantSoftenPresets.Custom, reloaded.Preset);
            Assert.Equal(-12, reloaded.AdynEq.ThresholdDb);
            Assert.Equal(3, reloaded.AdynEq.Ratio);
            Assert.Equal(0.42, reloaded.Deesser.Intensity);
        }

        [Fact]
        public void BuildConfig_Custom_ThresholdStaysInDb()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Custom);
            form.AdynEq.ThresholdDb = -26;

            var settings = SettingsOf(form);

            Assert.Equal(-26, settings.AdynEq!.ThresholdDb);
        }

        [Fact]
        public void Highpass_DisabledByDefault_AndOmittedFromParams()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Custom);

            Assert.False(form.HighpassEnabled);

            var settings = SettingsOf(form);

            Assert.Null(settings.AdynEq!.HighpassHz);
            Assert.Null(settings.Deesser!.HighpassHz);
        }

        [Fact]
        public void Highpass_Enabled_RoundTripsOnBothEngines()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Custom);
            form.HighpassEnabled = true;
            form.HighpassHz = 120;

            var reloaded = ConsonantSoftenForm.FromConfig(form.BuildConfig());

            Assert.True(reloaded.HighpassEnabled);
            Assert.Equal(120, reloaded.HighpassHz);

            var settings = SettingsOf(reloaded);
            Assert.Equal(120, settings.AdynEq!.HighpassHz);
            Assert.Equal(120, settings.Deesser!.HighpassHz);
        }

        [Fact]
        public void FromConfig_SavedCustom_KeepsSavedParamsInsteadOfReseeding()
        {
            var saved = AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften,
                enabled: true,
                new ConsonantSoftenSettings
                {
                    Engine = ConsonantSoftenEngines.AdynEq,
                    Preset = ConsonantSoftenPresets.Custom,
                    AdynEq = new AdynEqParams { ThresholdDb = -18, Ratio = 5 },
                });

            var form = ConsonantSoftenForm.FromConfig(saved);

            Assert.Equal(-18, form.AdynEq.ThresholdDb);
            Assert.Equal(5, form.AdynEq.Ratio);
        }

        [Fact]
        public void SetEngine_KeepsPresetAndDrafts()
        {
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetPreset(ConsonantSoftenPresets.Custom);
            form.Deesser.Intensity = 0.9;

            form.SetEngine(ConsonantSoftenEngines.Deesser);

            Assert.Equal(ConsonantSoftenPresets.Custom, form.Preset);
            Assert.Equal(0.9, form.Deesser.Intensity);
            Assert.Equal(ConsonantSoftenEngines.Deesser, SettingsOf(form).Engine);
        }
    }
}
