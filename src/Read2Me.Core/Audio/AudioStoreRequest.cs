using Read2Me.Core.Models;

namespace Read2Me.Core.Audio
{
    public sealed class AudioStoreRequest
    {
        public ProjectFolderId FolderId { get; init; }
        public Guid CharacterId { get; init; }
        public string CharacterName { get; init; } = string.Empty;
        public IReadOnlyList<string> CharacterAliases { get; init; } = [];
        public Guid VoiceId { get; init; }
        public string VoiceName { get; init; } = string.Empty;
        public Stream Source { get; init; } = Stream.Null;
        public string Extension { get; init; } = string.Empty;
    }
}
