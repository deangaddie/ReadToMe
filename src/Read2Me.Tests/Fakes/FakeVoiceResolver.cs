using Read2Me.Core.Models;
using Read2Me.Services.Voice;

namespace Read2Me.Tests.Fakes;

public sealed class FakeVoiceResolver : IVoiceResolver
{
    private readonly Dictionary<Guid, Guid?> _map = new();
    private readonly Dictionary<Guid, string?> _nameMap = new();

    public void SetVoice(Guid itemId, Guid? voiceId) => _map[itemId] = voiceId;
    public void SetName(Guid itemId, string? name) => _nameMap[itemId] = name;

    public Task<IReadOnlyDictionary<Guid, Guid?>> ResolveAsync(
        ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, Guid?>();
        foreach (var id in itemIds)
            result[id] = _map.TryGetValue(id, out var v) ? v : null;
        return Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(result);
    }

    public Task<IReadOnlyDictionary<Guid, string?>> ResolveNamesAsync(
        ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, string?>();
        foreach (var id in itemIds)
            result[id] = _nameMap.TryGetValue(id, out var n) ? n : null;
        return Task.FromResult<IReadOnlyDictionary<Guid, string?>>(result);
    }
}
