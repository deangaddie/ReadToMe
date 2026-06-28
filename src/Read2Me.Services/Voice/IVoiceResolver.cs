using Read2Me.Core.Models;

namespace Read2Me.Services.Voice;

public interface IVoiceResolver
{
    // itemId -> resolved VoiceId (null = no voice resolves)
    Task<IReadOnlyDictionary<Guid, Guid?>> ResolveAsync(
        ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    // itemId -> resolved Voice name (null = no voice). ResolveAsync + one batched name lookup.
    Task<IReadOnlyDictionary<Guid, string?>> ResolveNamesAsync(
        ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}
