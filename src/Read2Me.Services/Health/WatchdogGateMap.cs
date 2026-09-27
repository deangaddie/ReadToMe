using System;
using System.Collections.Generic;

namespace Read2Me.Services.Health;

/// <summary>
/// The service→gates association passed to the monitor at construction: llama → the
/// <c>QueuedParagraph</c> gate; TTS/whisper/similarity services → the <c>QueuedAudioItem</c> gate.
/// Built in DI from the registry via <see cref="ForServices"/>; a service absent from the map is untracked.
/// </summary>
public sealed class WatchdogGateMap
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IWatchdogGate>> _map;

    public WatchdogGateMap(IReadOnlyDictionary<string, IReadOnlyList<IWatchdogGate>> map) => _map = map;

    /// <summary>
    /// The production mapping: llama gates the paragraph (character-attribution) queue; every other
    /// service (TTS incl. audio.cpp, whisper, similarity) gates the audio queue.
    /// </summary>
    public static WatchdogGateMap ForServices(
        IEnumerable<DockerAiService> services, IWatchdogGate paragraphGate, IWatchdogGate audioGate)
    {
        var map = new Dictionary<string, IReadOnlyList<IWatchdogGate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in services)
        {
            map[svc.Name] = svc.Name.Equals("llama", StringComparison.OrdinalIgnoreCase)
                ? [paragraphGate]
                : [audioGate];
        }
        return new WatchdogGateMap(map);
    }

    /// <summary>Gates that must close while <paramref name="serviceName"/> recovers; empty if unmapped.</summary>
    public IReadOnlyList<IWatchdogGate> GatesFor(string serviceName) =>
        _map.TryGetValue(serviceName, out var gates) ? gates : Array.Empty<IWatchdogGate>();

    /// <summary>Whether the watchdog manages this service at all.</summary>
    public bool Contains(string serviceName) => _map.ContainsKey(serviceName);
}
