namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// One audio.cpp <c>POST /v1/audio/speech</c>, model-neutral: each provider maps its typed
    /// settings onto these fields. <see cref="Options"/> values are strings because audio.cpp
    /// accepts nothing else there.
    /// </summary>
    /// <param name="ModelId">The <c>server.json</c> model entry; switching models is audio.cpp's job.</param>
    /// <param name="Input">The text to speak.</param>
    /// <param name="VoiceRef">Reference audio for cloning, sent base64; null for models that design a voice.</param>
    /// <param name="ReferenceText">The reference audio's transcript, for models that use it; null omits it.</param>
    /// <param name="Language">
    /// The top-level <c>language</c> hint, which audio.cpp hands the model as the input text's
    /// language (an <c>options.language</c> never reaches it); null omits it.
    /// </param>
    public sealed record AudioCppSpeechRequest(
        string ModelId,
        string Input,
        byte[]? VoiceRef,
        string? ReferenceText,
        IReadOnlyDictionary<string, string> Options,
        string? Language = null);
}
