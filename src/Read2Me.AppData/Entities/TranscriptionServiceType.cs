namespace Read2Me.AppData.Entities
{
    /// <summary>
    /// Discriminator selecting which transcription backend a
    /// <see cref="TranscriptionServiceConfig"/> targets. The chosen type also
    /// determines the shape of the type-specific settings blob.
    /// </summary>
    public enum TranscriptionServiceType
    {
        /// <summary>Local Whisper ASR web service (Infra read2me-whisper).</summary>
        LocalWhisper = 0,
    }
}
