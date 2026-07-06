namespace Read2Me.Services.Audio.Transcription
{
    /// <summary>A single transcribed word with its start/end offsets in seconds.</summary>
    public readonly record struct TranscribedWord(string Word, double Start, double End);
}
