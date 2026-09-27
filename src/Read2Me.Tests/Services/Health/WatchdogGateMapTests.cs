using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class WatchdogGateMapTests
{
    private static readonly DockerAiServiceRegistry Registry = new();
    private static readonly StubGate ParagraphGate = new();
    private static readonly StubGate AudioGate = new();

    private static WatchdogGateMap Map() => WatchdogGateMap.ForServices(Registry.All, ParagraphGate, AudioGate);

    [Fact]
    public void Llama_HoldsParagraphGate()
    {
        Assert.Same(ParagraphGate, Assert.Single(Map().GatesFor("llama")));
    }

    [Fact]
    public void AudioCpp_HoldsAudioGate()
    {
        var map = Map();

        Assert.True(map.Contains("audiocpp"));
        Assert.Same(AudioGate, Assert.Single(map.GatesFor("audiocpp")));
    }

    [Fact]
    public void EveryRegisteredService_IsMapped()
    {
        var map = Map();

        foreach (var svc in Registry.All)
            Assert.True(map.Contains(svc.Name), $"{svc.Name} should be mapped");
    }

    private sealed class StubGate : IWatchdogGate
    {
        public void Close(string reason) { }
        public void Open() { }
        public bool IsOpen => true;
        public string? CloseReason => null;
        public bool HasPendingWork => false;
    }
}
