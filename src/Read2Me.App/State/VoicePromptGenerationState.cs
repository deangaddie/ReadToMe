using System;

namespace Read2Me.App.State
{
    /// <summary>
    /// Circuit-scoped signal that a single (per-voice) voice-prompt LLM generation is
    /// running. The character detail panel raises it around a regenerate call so the
    /// status dock can show an expandable live-stream row — mirroring the batch and
    /// character-attribution flows, which already surface the LLM stream.
    /// </summary>
    public sealed class VoicePromptGenerationState
    {
        public bool IsRunning { get; private set; }
        public string? CharacterName { get; private set; }

        public event Action? Changed;

        public void Begin(string characterName)
        {
            IsRunning = true;
            CharacterName = characterName;
            Changed?.Invoke();
        }

        public void End()
        {
            IsRunning = false;
            CharacterName = null;
            Changed?.Invoke();
        }
    }
}
