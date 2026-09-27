using System.Globalization;

namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// The <c>seed</c> option for an audio.cpp request. audio.cpp defaults to a fixed 0, and the
    /// pipeline's WER retries resend identical arguments, so an unpinned request draws a fresh
    /// random seed — a retry then gets a genuinely different take.
    /// </summary>
    public static class AudioCppSeed
    {
        public static string For(int? pinned) =>
            (pinned ?? Random.Shared.Next()).ToString(CultureInfo.InvariantCulture);
    }
}
