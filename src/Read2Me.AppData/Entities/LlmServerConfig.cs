namespace Read2Me.AppData.Entities
{
    public class LlmServerConfig
    {
        public int Id { get; set; }

        /// <summary>User-facing name for this configuration.</summary>
        public string Name { get; set; } = string.Empty;

        public LlmApiType ApiType { get; set; } = LlmApiType.OpenAiCompatible;

        /// <summary>Server base URL, e.g. http://localhost:8080.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>Optional bearer token sent as Authorization header.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Optional model id. Omitted from the request when null/blank.</summary>
        public string? Model { get; set; }

        // Optional request parameters. Null => not sent, server default applies.
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? MaxTokens { get; set; }
        public double? FrequencyPenalty { get; set; }
        public double? PresencePenalty { get; set; }
    }
}
