using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;

namespace Read2Me.App.Shared
{
    public sealed class SubstitutionStepFormItem
    {
        public string Id { get; set; } = "";
        public string FromText { get; set; } = "";
        public string ToText { get; set; } = "";
    }

    public sealed class ToSentenceCaseFormItem
    {
        public bool ParagraphEnabled { get; set; } = true;
        public bool WordEnabled { get; set; } = true;
        public int WordMinLength { get; set; } = 5;
    }

    public sealed class ParagraphTtsServiceConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ParagraphTtsServiceType Type { get; set; } = ParagraphTtsServiceType.VoxCpm2;

        // VoxCpm2 connection/app fields owned by the form (not the editor).
        public string BaseUrl { get; set; } = "";
        public int MaxChunkChars { get; set; } = 500;
        public bool CarrierPrefixEnabled { get; set; }
        public int CarrierMaxTargetChars { get; set; } = 30;

        // VoxCpm2 tunable params — full JSON the editor binds to (BaseUrl/MaxChunkChars held separately above).
        public string? SettingsJson { get; set; }

        public List<string> EnabledStepIds { get; set; } = [];
        public List<SubstitutionStepFormItem> SubstitutionSteps { get; set; } = [];
        public ToSentenceCaseFormItem ToSentenceCase { get; set; } = new();

        public bool IsStepEnabled(string id) => EnabledStepIds.Contains(id);

        // Newly added substitutions default to enabled so they take effect immediately.
        public void AddSubstitution()
        {
            var id = Guid.NewGuid().ToString();
            SubstitutionSteps.Add(new SubstitutionStepFormItem { Id = id });
            EnabledStepIds.Add(id);
        }

        public void RemoveSubstitution(string id)
        {
            SubstitutionSteps.RemoveAll(s => s.Id == id);
            EnabledStepIds.Remove(id);
        }

        public void SetStepEnabled(string id, bool enabled)
        {
            if (enabled)
            {
                if (!EnabledStepIds.Contains(id))
                    EnabledStepIds.Add(id);
            }
            else
            {
                EnabledStepIds.Remove(id);
            }
        }

        public static ParagraphTtsServiceConfigForm FromConfig(ParagraphTtsServiceConfig c)
        {
            var form = new ParagraphTtsServiceConfigForm
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type,
                EnabledStepIds = [.. c.EnabledStepIds],
                SubstitutionSteps = [.. c.SubstitutionSteps
                    .OrderBy(s => s.Order)
                    .Select(s => new SubstitutionStepFormItem { Id = s.Id, FromText = s.FromText, ToText = s.ToText })],
                ToSentenceCase = c.ToSentenceCaseConfig is { } tsc
                    ? new ToSentenceCaseFormItem
                    {
                        ParagraphEnabled = tsc.ParagraphEnabled,
                        WordEnabled = tsc.WordEnabled,
                        WordMinLength = tsc.WordMinLength,
                    }
                    : new ToSentenceCaseFormItem(),
            };

            switch (c.Type)
            {
                case ParagraphTtsServiceType.VoxCpm2:
                    var s = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? VoxCpm2ParagraphTtsSettings.Recommended
                        : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(c.SettingsJson) ?? VoxCpm2ParagraphTtsSettings.Recommended;
                    form.BaseUrl = s.BaseUrl;
                    form.MaxChunkChars = s.MaxChunkChars;
                    form.CarrierPrefixEnabled = s.CarrierPrefixEnabled;
                    form.CarrierMaxTargetChars = s.CarrierMaxTargetChars;
                    form.SettingsJson = JsonSerializer.Serialize(s);
                    break;

                case ParagraphTtsServiceType.Chatterbox:
                    var cb = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? ChatterboxParagraphTtsSettings.Recommended
                        : JsonSerializer.Deserialize<ChatterboxParagraphTtsSettings>(c.SettingsJson) ?? ChatterboxParagraphTtsSettings.Recommended;
                    form.BaseUrl = cb.BaseUrl;
                    form.MaxChunkChars = cb.MaxChunkChars;
                    form.CarrierPrefixEnabled = cb.CarrierPrefixEnabled;
                    form.CarrierMaxTargetChars = cb.CarrierMaxTargetChars;
                    form.SettingsJson = JsonSerializer.Serialize(cb);
                    break;

                case ParagraphTtsServiceType.ChatterboxTurbo:
                    var cbt = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? ChatterboxTurboParagraphTtsSettings.Recommended
                        : JsonSerializer.Deserialize<ChatterboxTurboParagraphTtsSettings>(c.SettingsJson) ?? ChatterboxTurboParagraphTtsSettings.Recommended;
                    form.BaseUrl = cbt.BaseUrl;
                    form.MaxChunkChars = cbt.MaxChunkChars;
                    form.CarrierPrefixEnabled = cbt.CarrierPrefixEnabled;
                    form.CarrierMaxTargetChars = cbt.CarrierMaxTargetChars;
                    form.SettingsJson = JsonSerializer.Serialize(cbt);
                    break;

                case ParagraphTtsServiceType.Qwen3Base:
                    var qb = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? Qwen3ParagraphTtsSettings.Recommended
                        : JsonSerializer.Deserialize<Qwen3ParagraphTtsSettings>(c.SettingsJson) ?? Qwen3ParagraphTtsSettings.Recommended;
                    form.BaseUrl = qb.BaseUrl;
                    form.MaxChunkChars = qb.MaxChunkChars;
                    form.CarrierPrefixEnabled = qb.CarrierPrefixEnabled;
                    form.CarrierMaxTargetChars = qb.CarrierMaxTargetChars;
                    form.SettingsJson = JsonSerializer.Serialize(qb);
                    break;
            }

            return form;
        }

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "Name is required.";

            switch (Type)
            {
                case ParagraphTtsServiceType.VoxCpm2:
                case ParagraphTtsServiceType.Chatterbox:
                case ParagraphTtsServiceType.ChatterboxTurbo:
                case ParagraphTtsServiceType.Qwen3Base:
                    if (string.IsNullOrWhiteSpace(BaseUrl))
                        return "Base URL is required.";
                    if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
                        return "Base URL must be a valid absolute URL (e.g. http://localhost:8000).";
                    break;
            }

            return null;
        }

        public ParagraphTtsServiceConfig BuildConfig()
        {
            var settingsJson = Type switch
            {
                ParagraphTtsServiceType.VoxCpm2 => BuildVoxCpm2SettingsJson(),
                ParagraphTtsServiceType.Chatterbox => BuildChatterboxSettingsJson(),
                ParagraphTtsServiceType.ChatterboxTurbo => BuildChatterboxTurboSettingsJson(),
                ParagraphTtsServiceType.Qwen3Base => BuildQwen3BaseSettingsJson(),
                _ => throw new NotSupportedException($"Unsupported paragraph TTS type '{Type}'."),
            };

            return new ParagraphTtsServiceConfig
            {
                Id = Id,
                Name = Name.Trim(),
                Type = Type,
                SettingsJson = settingsJson,
                EnabledStepIds = [.. EnabledStepIds],
                SubstitutionSteps = SubstitutionSteps
                    .Select((s, i) => new TextSubstitutionStep { Id = s.Id, FromText = s.FromText, ToText = s.ToText, Order = i })
                    .ToList(),
                ToSentenceCaseConfig = EnabledStepIds.Contains("to-sentence-case")
                    ? new ToSentenceCaseConfig
                    {
                        ParagraphEnabled = ToSentenceCase.ParagraphEnabled,
                        WordEnabled = ToSentenceCase.WordEnabled,
                        WordMinLength = ToSentenceCase.WordMinLength,
                    }
                    : null,
            };
        }

        // BaseUrl + MaxChunkChars owned by the form, the 9 tunable params by the editor's SettingsJson — merge here.
        private string BuildVoxCpm2SettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? VoxCpm2ParagraphTtsSettings.Recommended
                : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(SettingsJson)
                  ?? VoxCpm2ParagraphTtsSettings.Recommended;

            settings = settings with
            {
                BaseUrl = BaseUrl.Trim(),
                MaxChunkChars = MaxChunkChars,
                CarrierPrefixEnabled = CarrierPrefixEnabled,
                CarrierMaxTargetChars = CarrierMaxTargetChars,
            };
            return JsonSerializer.Serialize(settings);
        }

        // BaseUrl + MaxChunkChars owned by the form, the 6 tunable params by the editor's SettingsJson — merge here.
        private string BuildChatterboxSettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? ChatterboxParagraphTtsSettings.Recommended
                : JsonSerializer.Deserialize<ChatterboxParagraphTtsSettings>(SettingsJson)
                  ?? ChatterboxParagraphTtsSettings.Recommended;

            settings = settings with
            {
                BaseUrl = BaseUrl.Trim(),
                MaxChunkChars = MaxChunkChars,
                CarrierPrefixEnabled = CarrierPrefixEnabled,
                CarrierMaxTargetChars = CarrierMaxTargetChars,
            };
            return JsonSerializer.Serialize(settings);
        }

        // BaseUrl + MaxChunkChars owned by the form, the 2 tunable params by the editor's SettingsJson — merge here.
        private string BuildChatterboxTurboSettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? ChatterboxTurboParagraphTtsSettings.Recommended
                : JsonSerializer.Deserialize<ChatterboxTurboParagraphTtsSettings>(SettingsJson)
                  ?? ChatterboxTurboParagraphTtsSettings.Recommended;

            settings = settings with
            {
                BaseUrl = BaseUrl.Trim(),
                MaxChunkChars = MaxChunkChars,
                CarrierPrefixEnabled = CarrierPrefixEnabled,
                CarrierMaxTargetChars = CarrierMaxTargetChars,
            };
            return JsonSerializer.Serialize(settings);
        }

        // BaseUrl + MaxChunkChars owned by the form, the language + 5 sampling params by the editor's SettingsJson — merge here.
        private string BuildQwen3BaseSettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? Qwen3ParagraphTtsSettings.Recommended
                : JsonSerializer.Deserialize<Qwen3ParagraphTtsSettings>(SettingsJson)
                  ?? Qwen3ParagraphTtsSettings.Recommended;

            settings = settings with
            {
                BaseUrl = BaseUrl.Trim(),
                MaxChunkChars = MaxChunkChars,
                CarrierPrefixEnabled = CarrierPrefixEnabled,
                CarrierMaxTargetChars = CarrierMaxTargetChars,
            };
            return JsonSerializer.Serialize(settings);
        }
    }
}
