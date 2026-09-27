namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// The audio.cpp endpoint kept answering 503 <c>server_busy</c> — it is generating with a
    /// different model — after every retry. The provider is alive, not down: the audio pipeline
    /// turns this into <c>WorkOutcome.Busy</c>, never an outage for the watchdog.
    /// </summary>
    public sealed class TtsBusyException(string baseUrl, string modelId)
        : Exception($"TTS busy: {baseUrl} is generating with another model; {modelId} will retry.")
    {
        public string BaseUrl { get; } = baseUrl;
        public string ModelId { get; } = modelId;
    }
}
