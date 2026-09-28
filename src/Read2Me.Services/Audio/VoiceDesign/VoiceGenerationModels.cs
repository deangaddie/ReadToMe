using Read2Me.Core.Models;
using Read2Me.Services.Audio.AudioCpp;

namespace Read2Me.Services.Audio.VoiceDesign
{
    public sealed class VoiceGenerationRequest
    {
        public required ProjectFolderId FolderId { get; init; }
        public required Guid CharacterId { get; init; }
        public string CharacterName { get; init; } = string.Empty;
        public IReadOnlyList<string> CharacterAliases { get; init; } = [];
        public required Guid VoiceId { get; init; }
        public required string VoiceName { get; init; }
        public required string DesignPrompt { get; init; }
        public string? SettingsOverrideJson { get; init; }
    }

    public sealed class VoiceGenerationResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public string? AudioFileName { get; init; }
        public string? Transcript { get; init; }

        /// <summary>The stored take's length, or null when its header could not be read.</summary>
        public double? ReferenceSeconds { get; init; }

        /// <summary>
        /// Set when the take is over the soft <see cref="ReferenceLimit"/>: it was kept, and the
        /// caller says so. A take over the hard limit is a <see cref="Failure"/> instead.
        /// </summary>
        public string? ReferenceWarning { get; init; }

        public static VoiceGenerationResult Success(string audioFileName, string transcript, double? durationMs = null) => new()
        {
            IsSuccess = true,
            AudioFileName = audioFileName,
            Transcript = transcript,
            ReferenceSeconds = durationMs is { } ms ? Math.Round(ms / 1000, 1) : null,
            ReferenceWarning = ReferenceLimit.SoftWarning(durationMs),
        };

        /// <summary>
        /// The voice-design provider was alive but busy with another model — nothing failed, a
        /// retry later will do. Callers surface it as "try again", not as a generation failure.
        /// </summary>
        public bool IsBusy { get; init; }

        public static VoiceGenerationResult Failure(string message) => new()
        {
            IsSuccess = false,
            ErrorMessage = message
        };

        public static VoiceGenerationResult Busy() => new()
        {
            IsSuccess = false,
            IsBusy = true,
            ErrorMessage = TtsBusyException.UserMessage
        };
    }
}
