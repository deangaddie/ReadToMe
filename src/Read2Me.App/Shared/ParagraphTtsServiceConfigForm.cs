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

    public sealed class ParagraphTtsServiceConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ParagraphTtsServiceType Type { get; set; } = ParagraphTtsServiceType.VoxCpm2;

        // VoxCpm2 connection/app fields owned by the form (not the editor).
        public string BaseUrl { get; set; } = "";
        public int MaxChunkChars { get; set; } = 500;

        // VoxCpm2 tunable params — full JSON the editor binds to (BaseUrl/MaxChunkChars held separately above).
        public string? SettingsJson { get; set; }

        public List<string> EnabledStepIds { get; set; } = [];
        public List<SubstitutionStepFormItem> SubstitutionSteps { get; set; } = [];

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
            };

            switch (c.Type)
            {
                case ParagraphTtsServiceType.VoxCpm2:
                    var s = string.IsNullOrWhiteSpace(c.SettingsJson)
                        ? VoxCpm2ParagraphTtsSettings.Recommended
                        : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(c.SettingsJson) ?? VoxCpm2ParagraphTtsSettings.Recommended;
                    form.BaseUrl = s.BaseUrl;
                    form.MaxChunkChars = s.MaxChunkChars;
                    form.SettingsJson = JsonSerializer.Serialize(s);
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
            };
        }

        // BaseUrl + MaxChunkChars owned by the form, the 9 tunable params by the editor's SettingsJson — merge here.
        private string BuildVoxCpm2SettingsJson()
        {
            var settings = string.IsNullOrWhiteSpace(SettingsJson)
                ? VoxCpm2ParagraphTtsSettings.Recommended
                : JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(SettingsJson)
                  ?? VoxCpm2ParagraphTtsSettings.Recommended;

            settings = settings with { BaseUrl = BaseUrl.Trim(), MaxChunkChars = MaxChunkChars };
            return JsonSerializer.Serialize(settings);
        }
    }
}
