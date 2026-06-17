using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.Transcription
{
    /// <summary>
    /// Selects the <see cref="ITranscriptionClient"/> implementation for a given
    /// backend type. This is the seam that makes new transcription backends
    /// drop-in: register a keyed client and the resolver picks it up.
    /// </summary>
    public interface ITranscriptionClientResolver
    {
        ITranscriptionClient Resolve(TranscriptionServiceType type);
    }
}
