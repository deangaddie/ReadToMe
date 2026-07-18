using System;
using System.Globalization;
using Read2Me.AppData.Entities;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for an <see cref="LlmServerConfig"/>. Numeric request params are held
    /// as strings so a blank field means "omit" (server default applies).
    /// </summary>
    public sealed class LlmServerConfigForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public LlmApiType ApiType { get; set; } = LlmApiType.OpenAiCompatible;
        public string BaseUrl { get; set; } = "";
        public string? ApiKey { get; set; }
        public string? Model { get; set; }

        public string? Temperature { get; set; }
        public string? TopP { get; set; }
        public string? MaxTokens { get; set; }
        public string? FrequencyPenalty { get; set; }
        public string? PresencePenalty { get; set; }

        /// <summary>Paragraphs per attribution request; blank means 1 (single-paragraph mode).</summary>
        public string? AttributionBatchSize { get; set; }

        /// <summary>Attribution prompt tier this server uses.</summary>
        public AttributionPromptStyle PromptStyle { get; set; } = AttributionPromptStyle.Full;

        /// <summary>True when this endpoint can switch the loaded model on demand (llama.cpp autoload).</summary>
        public bool SupportsModelSwitch { get; set; }

        public static LlmServerConfigForm FromConfig(LlmServerConfig c) => new()
        {
            Id = c.Id,
            Name = c.Name,
            ApiType = c.ApiType,
            BaseUrl = c.BaseUrl,
            ApiKey = c.ApiKey,
            Model = c.Model,
            Temperature = c.Temperature?.ToString(CultureInfo.InvariantCulture),
            TopP = c.TopP?.ToString(CultureInfo.InvariantCulture),
            MaxTokens = c.MaxTokens?.ToString(CultureInfo.InvariantCulture),
            FrequencyPenalty = c.FrequencyPenalty?.ToString(CultureInfo.InvariantCulture),
            PresencePenalty = c.PresencePenalty?.ToString(CultureInfo.InvariantCulture),
            AttributionBatchSize = c.AttributionBatchSize.ToString(CultureInfo.InvariantCulture),
            PromptStyle = c.PromptStyle,
            SupportsModelSwitch = c.SupportsModelSwitch,
        };

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "Name is required.";
            if (string.IsNullOrWhiteSpace(BaseUrl))
                return "Base URL is required.";
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
                return "Base URL must be a valid absolute URL (e.g. http://localhost:8080).";

            if (!TryParseDouble(Temperature, out _)) return "Temperature must be a number.";
            if (!TryParseDouble(TopP, out _)) return "Top P must be a number.";
            if (!TryParseInt(MaxTokens, out _)) return "Max tokens must be a whole number.";
            if (!TryParseDouble(FrequencyPenalty, out _)) return "Frequency penalty must be a number.";
            if (!TryParseDouble(PresencePenalty, out _)) return "Presence penalty must be a number.";
            if (!TryParseInt(AttributionBatchSize, out var batchSize) || batchSize is < 1)
                return "Paragraphs per request must be a whole number of 1 or more.";

            return null;
        }

        public LlmServerConfig BuildConfig()
        {
            TryParseDouble(Temperature, out var temp);
            TryParseDouble(TopP, out var topP);
            TryParseInt(MaxTokens, out var maxTokens);
            TryParseDouble(FrequencyPenalty, out var freq);
            TryParseDouble(PresencePenalty, out var pres);
            TryParseInt(AttributionBatchSize, out var batchSize);

            return new LlmServerConfig
            {
                Id = Id,
                Name = Name.Trim(),
                ApiType = ApiType,
                BaseUrl = BaseUrl.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
                Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
                Temperature = temp,
                TopP = topP,
                MaxTokens = maxTokens,
                FrequencyPenalty = freq,
                PresencePenalty = pres,
                AttributionBatchSize = batchSize ?? 1,
                PromptStyle = PromptStyle,
                SupportsModelSwitch = SupportsModelSwitch,
            };
        }

        private static bool TryParseDouble(string? text, out double? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        private static bool TryParseInt(string? text, out int? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }
    }
}
