using System;
using System.Collections.Generic;

namespace Read2Me.Services.Health;

/// <summary>
/// The service→gates association passed to the monitor at construction: llama → the
/// <c>QueuedParagraph</c> gate; TTS/whisper/similarity services → the <c>QueuedAudioItem</c> gate.
/// Built in DI from the registry; a service absent from the map is untracked (no gate to hold).
/// </summary>
public sealed class WatchdogGateMap
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IWatchdogGate>> _map;

    public WatchdogGateMap(IReadOnlyDictionary<string, IReadOnlyList<IWatchdogGate>> map) => _map = map;

    /// <summary>Gates that must close while <paramref name="serviceName"/> recovers; empty if unmapped.</summary>
    public IReadOnlyList<IWatchdogGate> GatesFor(string serviceName) =>
        _map.TryGetValue(serviceName, out var gates) ? gates : Array.Empty<IWatchdogGate>();

    /// <summary>Whether the watchdog manages this service at all.</summary>
    public bool Contains(string serviceName) => _map.ContainsKey(serviceName);
}
