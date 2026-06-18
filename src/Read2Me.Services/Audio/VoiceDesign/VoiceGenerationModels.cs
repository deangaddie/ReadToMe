using System;
using System.Collections.Generic;
using Read2Me.Core.Models;

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

        public static VoiceGenerationResult Success(string audioFileName, string transcript) => new()
        {
            IsSuccess = true,
            AudioFileName = audioFileName,
            Transcript = transcript
        };

        public static VoiceGenerationResult Failure(string message) => new()
        {
            IsSuccess = false,
            ErrorMessage = message
        };
    }
}
