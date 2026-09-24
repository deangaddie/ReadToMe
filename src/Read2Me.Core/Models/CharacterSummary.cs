namespace Read2Me.Core.Models;

/// <summary>An alias by id, so a list row can offer its removal without a second read.</summary>
public readonly record struct CharacterAliasRef(Guid Id, string Name);

/// <summary>
/// One cast-list row: a character with everything the roster shows about it — aliases, how many
/// lines it speaks, and how many of its voices are planned versus ready for TTS. The narrator link
/// is not here; it is a project-level fact the caller overlays.
/// </summary>
public sealed record CharacterSummary(
    Guid Id,
    string Name,
    IReadOnlyList<CharacterAliasRef> Aliases,
    int LineCount,
    int VoiceCount,
    int ReadyVoiceCount,
    bool IsNarrator);
