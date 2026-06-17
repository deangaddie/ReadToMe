namespace Read2Me.AppData.Entities
{
    public class AudioServerConfig
    {
        public int Id { get; set; }

        /// <summary>User-facing name for this configuration.</summary>
        public string Name { get; set; } = string.Empty;

        public AudioServerRole Role { get; set; }

        /// <summary>Server base URL, e.g. http://localhost:9000.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>Optional bearer token sent as Authorization header.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Optional model id sent on requests that accept one.</summary>
        public string? Model { get; set; }
    }
}
