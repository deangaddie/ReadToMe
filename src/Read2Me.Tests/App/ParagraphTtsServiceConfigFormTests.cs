using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ParagraphTtsServiceConfigFormTests
    {
        private static ParagraphTtsServiceConfigForm Valid() => new()
        {
            Name = "Local VoxCpm2",
            Type = ParagraphTtsServiceType.VoxCpm2,
            BaseUrl = "http://localhost:8000",
            MaxChunkChars = 500,
        };

        // ---- MaxChunkChars default ----

        [Fact]
        public void FromConfig_SettingsJsonLackingMaxChunkChars_DefaultsTo500()
        {
            var json = JsonSerializer.Serialize(new { BaseUrl = "http://localhost:8000", MaxLen = 4096 });
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Old Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = json,
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(500, form.MaxChunkChars);
        }

        // ---- BuildConfig serializes MaxChunkChars ----

        [Fact]
        public void BuildConfig_SerializesMaxChunkCharsIntoSettingsJson()
        {
            var form = Valid();
            form.MaxChunkChars = 250;

            var config = form.BuildConfig();

            var settings = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson);
            Assert.NotNull(settings);
            Assert.Equal(250, settings!.MaxChunkChars);
        }

        // ---- Round-trip ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsMaxChunkChars()
        {
            var form = Valid();
            form.MaxChunkChars = 750;

            var config = form.BuildConfig();
            var round = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(750, round.MaxChunkChars);
        }

        // ---- SettingsJson carries all 9 params + BaseUrl + MaxChunkChars ----

        [Fact]
        public void BuildConfig_SettingsJsonContainsAllNineParamsPlusBaseUrlAndChunkChars()
        {
            var form = Valid();
            form.BaseUrl = "http://localhost:8000";
            form.MaxChunkChars = 250;
            // Editor binds full settings object (the 9 tunable params) to SettingsJson.
            form.SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended with
            {
                CfgValue = 3.5,
                InferenceTimesteps = 20,
                MinLen = 5,
                MaxLen = 2048,
                Normalize = true,
                Denoise = true,
                RetryBadcase = false,
                RetryBadcaseMaxTimes = 7,
                RetryBadcaseRatioThreshold = 4.0,
            });

            var config = form.BuildConfig();
            var s = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson);

            Assert.NotNull(s);
            Assert.Equal("http://localhost:8000", s!.BaseUrl);
            Assert.Equal(250, s.MaxChunkChars);
            Assert.Equal(3.5, s.CfgValue);
            Assert.Equal(20, s.InferenceTimesteps);
            Assert.Equal(5, s.MinLen);
            Assert.Equal(2048, s.MaxLen);
            Assert.True(s.Normalize);
            Assert.True(s.Denoise);
            Assert.False(s.RetryBadcase);
            Assert.Equal(7, s.RetryBadcaseMaxTimes);
            Assert.Equal(4.0, s.RetryBadcaseRatioThreshold);
        }

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsAllNineParams()
        {
            var original = VoxCpm2ParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://localhost:8000",
                CfgValue = 2.7,
                InferenceTimesteps = 15,
                MinLen = 4,
                MaxLen = 1024,
                Normalize = true,
                Denoise = true,
                RetryBadcase = false,
                RetryBadcaseMaxTimes = 5,
                RetryBadcaseRatioThreshold = 8.0,
                MaxChunkChars = 333,
            };
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(original),
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);
            var rebuilt = form.BuildConfig();
            var s = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(rebuilt.SettingsJson);

            Assert.Equal(original, s);
        }

        // ---- FromConfig reads MaxChunkChars ----

        [Fact]
        public void FromConfig_ReadsMaxChunkCharsFromSettingsJson()
        {
            var json = JsonSerializer.Serialize(new VoxCpm2ParagraphTtsSettings
            {
                BaseUrl = "http://localhost:8000",
                MaxLen = 4096,
                MaxChunkChars = 1200,
            });
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = json,
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(1200, form.MaxChunkChars);
        }

        // ---- Carrier prefix ----

        [Fact]
        public void FromConfig_SettingsJsonLackingCarrierFields_UsesDefaults()
        {
            var json = JsonSerializer.Serialize(new { BaseUrl = "http://localhost:8000", MaxLen = 4096 });
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Old Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = json,
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.False(form.CarrierPrefixEnabled);
            Assert.Equal(30, form.CarrierMaxTargetChars);
        }

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsCarrierFields()
        {
            var form = Valid();
            form.CarrierPrefixEnabled = true;
            form.CarrierMaxTargetChars = 42;

            var config = form.BuildConfig();
            var round = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.True(round.CarrierPrefixEnabled);
            Assert.Equal(42, round.CarrierMaxTargetChars);
        }

        // ---- Chatterbox ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsChatterboxSettings()
        {
            var original = ChatterboxParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://localhost:8000",
                Exaggeration = 0.7,
                CfgWeight = 0.4,
                Temperature = 0.9,
                MinP = 0.1,
                TopP = 0.95,
                RepetitionPenalty = 1.5,
                MaxChunkChars = 333,
            };
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Chatterbox",
                Type = ParagraphTtsServiceType.Chatterbox,
                SettingsJson = JsonSerializer.Serialize(original),
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);
            Assert.Equal("http://localhost:8000", form.BaseUrl);
            Assert.Equal(333, form.MaxChunkChars);

            var rebuilt = form.BuildConfig();
            var s = JsonSerializer.Deserialize<ChatterboxParagraphTtsSettings>(rebuilt.SettingsJson);

            Assert.Equal(original, s);
        }

        [Fact]
        public void Validate_Chatterbox_RequiresBaseUrl()
        {
            var form = new ParagraphTtsServiceConfigForm
            {
                Name = "Chatterbox",
                Type = ParagraphTtsServiceType.Chatterbox,
                BaseUrl = "",
            };

            Assert.Equal("Base URL is required.", form.Validate());
        }

        // ---- ChatterboxTurbo ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsChatterboxTurboSettings()
        {
            var original = ChatterboxTurboParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://localhost:8001",
                Temperature = 0.9,
                RepetitionPenalty = 1.5,
                MaxChunkChars = 333,
            };
            var config = new ParagraphTtsServiceConfig
            {
                Name = "ChatterboxTurbo",
                Type = ParagraphTtsServiceType.ChatterboxTurbo,
                SettingsJson = JsonSerializer.Serialize(original),
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);
            Assert.Equal("http://localhost:8001", form.BaseUrl);
            Assert.Equal(333, form.MaxChunkChars);

            var rebuilt = form.BuildConfig();
            var s = JsonSerializer.Deserialize<ChatterboxTurboParagraphTtsSettings>(rebuilt.SettingsJson);

            Assert.Equal(original, s);
        }

        [Fact]
        public void Validate_ChatterboxTurbo_RequiresBaseUrl()
        {
            var form = new ParagraphTtsServiceConfigForm
            {
                Name = "ChatterboxTurbo",
                Type = ParagraphTtsServiceType.ChatterboxTurbo,
                BaseUrl = "",
            };

            Assert.Equal("Base URL is required.", form.Validate());
        }

        // ---- Qwen3Base ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsQwen3BaseSettings()
        {
            var original = Qwen3ParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://localhost:8101",
                ApiKey = "secret",
                Language = "en",
                Temperature = 0.7,
                TopP = 0.9,
                TopK = 40,
                RepetitionPenalty = 1.1,
                MaxNewTokens = 512,
                MaxChunkChars = 333,
            };
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Qwen3Base",
                Type = ParagraphTtsServiceType.Qwen3Base,
                SettingsJson = JsonSerializer.Serialize(original),
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);
            Assert.Equal("http://localhost:8101", form.BaseUrl);
            Assert.Equal(333, form.MaxChunkChars);

            var rebuilt = form.BuildConfig();
            var s = JsonSerializer.Deserialize<Qwen3ParagraphTtsSettings>(rebuilt.SettingsJson);

            Assert.Equal(original, s);
        }

        [Fact]
        public void Validate_Qwen3Base_RequiresBaseUrl()
        {
            var form = new ParagraphTtsServiceConfigForm
            {
                Name = "Qwen3Base",
                Type = ParagraphTtsServiceType.Qwen3Base,
                BaseUrl = "",
            };

            Assert.Equal("Base URL is required.", form.Validate());
        }

        // ---- EnabledStepIds ----

        [Fact]
        public void FromConfig_CopiesEnabledStepIds()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                EnabledStepIds = ["escape-parens"],
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(["escape-parens"], form.EnabledStepIds);
        }

        [Fact]
        public void FromConfig_EmptyEnabledStepIds_FormHasNone()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                EnabledStepIds = [],
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Empty(form.EnabledStepIds);
        }

        [Fact]
        public void BuildConfig_PreservesEnabledStepIds()
        {
            var form = Valid();
            form.EnabledStepIds = ["escape-parens"];

            var config = form.BuildConfig();

            Assert.Equal(["escape-parens"], config.EnabledStepIds);
        }

        [Fact]
        public void IsStepEnabled_TrueWhenIdPresent()
        {
            var form = Valid();
            form.EnabledStepIds = ["escape-parens"];

            Assert.True(form.IsStepEnabled("escape-parens"));
        }

        [Fact]
        public void IsStepEnabled_FalseWhenIdAbsent()
        {
            var form = Valid();
            form.EnabledStepIds = [];

            Assert.False(form.IsStepEnabled("escape-parens"));
        }

        [Fact]
        public void SetStepEnabled_True_AddsId()
        {
            var form = Valid();
            form.EnabledStepIds = [];

            form.SetStepEnabled("escape-parens", true);

            Assert.Contains("escape-parens", form.EnabledStepIds);
        }

        [Fact]
        public void SetStepEnabled_False_RemovesId()
        {
            var form = Valid();
            form.EnabledStepIds = ["escape-parens"];

            form.SetStepEnabled("escape-parens", false);

            Assert.DoesNotContain("escape-parens", form.EnabledStepIds);
        }

        [Fact]
        public void SetStepEnabled_True_Idempotent()
        {
            var form = Valid();
            form.EnabledStepIds = ["escape-parens"];

            form.SetStepEnabled("escape-parens", true);

            Assert.Equal(["escape-parens"], form.EnabledStepIds);
        }

        // ---- SubstitutionSteps ----

        [Fact]
        public void BuildConfig_WritesSubstitutionStepsWithOrderByIndex()
        {
            var form = Valid();
            form.SubstitutionSteps =
            [
                new() { Id = "id-a", FromText = "(", ToText = "," },
                new() { Id = "id-b", FromText = ")", ToText = "," },
            ];

            var config = form.BuildConfig();

            Assert.Equal(2, config.SubstitutionSteps.Count);
            var first = config.SubstitutionSteps.Single(s => s.Id == "id-a");
            var second = config.SubstitutionSteps.Single(s => s.Id == "id-b");
            Assert.Equal(0, first.Order);
            Assert.Equal(1, second.Order);
            Assert.Equal("(", first.FromText);
            Assert.Equal(",", first.ToText);
        }

        [Fact]
        public void SubstitutionSteps_RoundTrip_PreservesIdFromTextToTextAndOrder()
        {
            var id1 = Guid.NewGuid().ToString();
            var id2 = Guid.NewGuid().ToString();
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                SubstitutionSteps =
                [
                    new() { Id = id1, FromText = "(", ToText = ",", Order = 0 },
                    new() { Id = id2, FromText = ")", ToText = ",", Order = 1 },
                ],
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);
            var rebuilt = form.BuildConfig();

            Assert.Equal(2, rebuilt.SubstitutionSteps.Count);
            Assert.Equal(id1, rebuilt.SubstitutionSteps[0].Id);
            Assert.Equal(0, rebuilt.SubstitutionSteps[0].Order);
            Assert.Equal(id2, rebuilt.SubstitutionSteps[1].Id);
            Assert.Equal(1, rebuilt.SubstitutionSteps[1].Order);
        }

        [Fact]
        public void AddSubstitution_AppendsItemAndEnablesIt()
        {
            var form = Valid();

            form.AddSubstitution();

            Assert.Single(form.SubstitutionSteps);
            var item = form.SubstitutionSteps[0];
            Assert.False(string.IsNullOrEmpty(item.Id));
            Assert.True(form.IsStepEnabled(item.Id));
        }

        [Fact]
        public void AddSubstitution_MultipleCallsAppendInOrder()
        {
            var form = Valid();

            form.AddSubstitution();
            form.AddSubstitution();

            Assert.Equal(2, form.SubstitutionSteps.Count);
            Assert.NotEqual(form.SubstitutionSteps[0].Id, form.SubstitutionSteps[1].Id);
        }

        [Fact]
        public void RemoveSubstitution_RemovesItemAndDisablesIt()
        {
            var form = Valid();
            form.AddSubstitution();
            var id = form.SubstitutionSteps[0].Id;

            form.RemoveSubstitution(id);

            Assert.Empty(form.SubstitutionSteps);
            Assert.False(form.IsStepEnabled(id));
        }

        [Fact]
        public void RemoveSubstitution_OnlyRemovesTargetItem()
        {
            var form = Valid();
            form.AddSubstitution();
            form.AddSubstitution();
            var id0 = form.SubstitutionSteps[0].Id;
            var id1 = form.SubstitutionSteps[1].Id;

            form.RemoveSubstitution(id0);

            Assert.Single(form.SubstitutionSteps);
            Assert.Equal(id1, form.SubstitutionSteps[0].Id);
            Assert.True(form.IsStepEnabled(id1));
        }

        [Fact]
        public void EnableDisableSync_AddSubstitution_EnabledByDefault()
        {
            var form = Valid();
            form.AddSubstitution();
            var id = form.SubstitutionSteps[0].Id;

            Assert.True(form.IsStepEnabled(id));
        }

        [Fact]
        public void EnableDisableSync_SetStepEnabledFalse_RemovedFromEnabledStepIds()
        {
            var form = Valid();
            form.AddSubstitution();
            var id = form.SubstitutionSteps[0].Id;

            form.SetStepEnabled(id, false);

            Assert.False(form.IsStepEnabled(id));
            Assert.DoesNotContain(id, form.EnabledStepIds);
        }

        [Fact]
        public void EnableDisableSync_BuildConfig_ReflectsDisabledStep()
        {
            var form = Valid();
            form.AddSubstitution();
            var id = form.SubstitutionSteps[0].Id;
            form.SetStepEnabled(id, false);

            var config = form.BuildConfig();

            Assert.DoesNotContain(id, config.EnabledStepIds);
            Assert.Single(config.SubstitutionSteps);
        }

        [Fact]
        public void FromConfig_PopulatesSubstitutionStepsOrderedByOrder()
        {
            var id1 = Guid.NewGuid().ToString();
            var id2 = Guid.NewGuid().ToString();
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                SubstitutionSteps =
                [
                    new() { Id = id2, FromText = "(", ToText = ",", Order = 1 },
                    new() { Id = id1, FromText = ")", ToText = ",", Order = 0 },
                ],
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(2, form.SubstitutionSteps.Count);
            Assert.Equal(id1, form.SubstitutionSteps[0].Id);
            Assert.Equal(")", form.SubstitutionSteps[0].FromText);
            Assert.Equal(",", form.SubstitutionSteps[0].ToText);
            Assert.Equal(id2, form.SubstitutionSteps[1].Id);
        }

        // ---- ToSentenceCaseFormItem defaults ----

        [Fact]
        public void ToSentenceCaseFormItem_Defaults_AreCorrect()
        {
            var item = new ToSentenceCaseFormItem();

            Assert.True(item.ParagraphEnabled);
            Assert.True(item.WordEnabled);
            Assert.Equal(5, item.WordMinLength);
        }

        // ---- FromConfig hydrates ToSentenceCase ----

        [Fact]
        public void FromConfig_WithToSentenceCaseConfig_HydratesFormItem()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                ToSentenceCaseConfig = new ToSentenceCaseConfig
                {
                    ParagraphEnabled = false,
                    WordEnabled = true,
                    WordMinLength = 8,
                },
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.False(form.ToSentenceCase.ParagraphEnabled);
            Assert.True(form.ToSentenceCase.WordEnabled);
            Assert.Equal(8, form.ToSentenceCase.WordMinLength);
        }

        [Fact]
        public void FromConfig_WithoutToSentenceCaseConfig_UsesDefaults()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                ToSentenceCaseConfig = null,
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.True(form.ToSentenceCase.ParagraphEnabled);
            Assert.True(form.ToSentenceCase.WordEnabled);
            Assert.Equal(5, form.ToSentenceCase.WordMinLength);
        }

        // ---- BuildConfig attaches / omits ToSentenceCaseConfig ----

        [Fact]
        public void BuildConfig_StepEnabled_AttachesToSentenceCaseConfig()
        {
            var form = Valid();
            form.EnabledStepIds = ["to-sentence-case"];
            form.ToSentenceCase = new ToSentenceCaseFormItem
            {
                ParagraphEnabled = true,
                WordEnabled = false,
                WordMinLength = 3,
            };

            var config = form.BuildConfig();

            Assert.NotNull(config.ToSentenceCaseConfig);
            Assert.True(config.ToSentenceCaseConfig!.ParagraphEnabled);
            Assert.False(config.ToSentenceCaseConfig.WordEnabled);
            Assert.Equal(3, config.ToSentenceCaseConfig.WordMinLength);
        }

        [Fact]
        public void BuildConfig_StepDisabled_ToSentenceCaseConfigIsNull()
        {
            var form = Valid();
            form.EnabledStepIds = [];

            var config = form.BuildConfig();

            Assert.Null(config.ToSentenceCaseConfig);
        }

        [Fact]
        public void BuildConfig_ToSentenceCaseConfig_RoundTrips()
        {
            var original = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended),
                EnabledStepIds = ["to-sentence-case"],
                ToSentenceCaseConfig = new ToSentenceCaseConfig
                {
                    ParagraphEnabled = false,
                    WordEnabled = true,
                    WordMinLength = 7,
                },
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(original);
            var rebuilt = form.BuildConfig();

            Assert.NotNull(rebuilt.ToSentenceCaseConfig);
            Assert.False(rebuilt.ToSentenceCaseConfig!.ParagraphEnabled);
            Assert.True(rebuilt.ToSentenceCaseConfig.WordEnabled);
            Assert.Equal(7, rebuilt.ToSentenceCaseConfig.WordMinLength);
        }
    }
}
