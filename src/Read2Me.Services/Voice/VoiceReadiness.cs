using Read2Me.Data.Entities;

namespace Read2Me.Services.Voice
{
    /// <summary>
    /// When a Voice can be used for TTS: it has reference audio. A designed voice whose audio has
    /// not been synthesised yet, or an upload that never arrived, is planned but not ready. The
    /// cast list's <c>ready/total</c> chip, the narrator banner and the character summary endpoint
    /// all count with this one rule.
    /// </summary>
    public static class VoiceReadiness
    {
        public static bool IsReady(Data.Entities.Voice voice) => !string.IsNullOrEmpty(voice.AudioFileName);

        public static int ReadyCount(Character character) => character.Voices?.Count(IsReady) ?? 0;
    }
}
