using System;
using System.Collections.Generic;

namespace Read2Me.App.Characters;

public sealed record PromptWorkItem(Guid CharacterId, string CharacterName, bool IsNarrator = false);

public sealed record AudioWorkItem(
    Guid CharacterId,
    string CharacterName,
    IReadOnlyList<string> Aliases,
    Guid VoiceId,
    string VoiceName,
    string DesignPrompt,
    string? SettingsOverrideJson);
