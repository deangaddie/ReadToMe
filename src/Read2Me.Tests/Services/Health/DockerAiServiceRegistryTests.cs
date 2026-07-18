using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class DockerAiServiceRegistryTests
{
    private static readonly DockerAiServiceRegistry Registry = new();

    private const string LlamaBaseUrl = "http://localhost:8080";

    // The llama warm-up delegate hands the active config to the same IModelLoadGate real requests use,
    // so there is a single model-load path. These exercise the delegate directly with a fake gate.

    [Fact]
    public async Task LlamaWarmup_ActiveConfig_HandsConfigToGate()
    {
        var gate = new RecordingGate();
        var config = SwitchableConfig();
        var sut = Registry.GetByName("llama").Warmup!;

        await sut(BuildScope(config, gate), CancellationToken.None);

        Assert.Equal(1, gate.CallCount);
        Assert.Same(config, gate.LastConfig);
    }

    [Fact]
    public async Task LlamaWarmup_NoActiveConfig_DoesNotCallGate()
    {
        // Nothing configured → nothing safe to warm; health alone is readiness. No second load path.
        var gate = new RecordingGate();
        var sut = Registry.GetByName("llama").Warmup!;

        await sut(BuildScope(active: null, gate), CancellationToken.None);

        Assert.Equal(0, gate.CallCount);
    }

    [Fact]
    public async Task LlamaWarmup_NonSwitchableConfig_HandedToGate_WhichNoOps()
    {
        // A non-switchable active config still flows to the gate, which no-ops (see ModelLoadGateTests);
        // the warm-up returns cleanly → health alone is readiness. There is no separate warm path.
        var gate = new RecordingGate();
        var config = new LlmServerConfig { BaseUrl = LlamaBaseUrl, Model = "gemma-4b", SupportsModelSwitch = false };
        var sut = Registry.GetByName("llama").Warmup!;

        await sut(BuildScope(config, gate), CancellationToken.None);

        Assert.Same(config, gate.LastConfig);
    }

    [Fact]
    public async Task LlamaWarmup_ModelStillLoading_IsSoft_DoesNotThrow()
    {
        // Soft/retryable: the warm-up swallows ModelStillLoadingException so the probe reports readiness
        // (health alone) rather than a warm-up failure that would trip the watchdog / fail preflight.
        var gate = new RecordingGate
        {
            Behavior = c => throw new ModelStillLoadingException(
                c.BaseUrl, c.Model ?? string.Empty, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300)),
        };
        var sut = Registry.GetByName("llama").Warmup!;

        await sut(BuildScope(SwitchableConfig(), gate), CancellationToken.None);
    }

    [Fact]
    public async Task LlamaWarmup_ProviderException_Propagates()
    {
        // A genuinely unreachable endpoint is not soft: it propagates and the probe maps it to a
        // warm-up failure (the normal down/escalation path).
        var gate = new RecordingGate
        {
            Behavior = _ => throw new LlmProviderException("down", new HttpRequestException("boom")),
        };
        var sut = Registry.GetByName("llama").Warmup!;

        await Assert.ThrowsAsync<LlmProviderException>(
            () => sut(BuildScope(SwitchableConfig(), gate), CancellationToken.None));
    }

    private static LlmServerConfig SwitchableConfig() =>
        new() { BaseUrl = LlamaBaseUrl, Model = "gemma-26b_QAT", SupportsModelSwitch = true };

    private static IServiceProvider BuildScope(LlmServerConfig? active, IModelLoadGate gate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<LlmSettingsService>(new StubSettings(active));
        services.AddSingleton(gate);
        return services.BuildServiceProvider();
    }

    private sealed class StubSettings : LlmSettingsService
    {
        private readonly LlmServerConfig? _active;

        public StubSettings(LlmServerConfig? active)
            : base(null!, NullLogger<LlmSettingsService>.Instance) => _active = active;

        public override Task<LlmServerConfig?> GetActiveConfigAsync() => Task.FromResult(_active);
    }

    private sealed class RecordingGate : IModelLoadGate
    {
        public Func<LlmServerConfig, Task>? Behavior { get; set; }
        public int CallCount { get; private set; }
        public LlmServerConfig? LastConfig { get; private set; }

        public Task EnsureModelLoadedAsync(LlmServerConfig config, CancellationToken ct)
        {
            CallCount++;
            LastConfig = config;
            return Behavior?.Invoke(config) ?? Task.CompletedTask;
        }
    }

    [Fact]
    public void ContainsAllNineComposeServices()
    {
        var expected = new[]
        {
            ("llama",            "read2me-llama",          8080),
            ("chatterbox",       "read2me-chatterbox",     8000),
            ("chatterbox-turbo", "read2me-chatterbox-turbo", 8001),
            ("qwen3-tts",        "read2me-qwen3-tts",      8100),
            ("qwen3-tts-base",   "read2me-qwen3-tts-base", 8101),
            ("voxcpm2",          "read2me-voxcpm2",        8003),
            ("whisper",          "read2me-whisper",        9000),
            ("minilm-l6",        "read2me-minilm-l6",      8200),
            ("mpnet-base-v2",    "read2me-mpnet-base-v2",  8201),
        };

        foreach (var (name, container, port) in expected)
        {
            var svc = Registry.GetByName(name);
            Assert.Equal(container, svc.ContainerName);
            Assert.Equal($"http://localhost:{port}", svc.BaseUrl);
        }
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8080")]
    public void TryGetByBaseUrl_ResolvesLlama_ToleratingSlashAndLoopbackForms(string baseUrl)
    {
        Assert.True(Registry.TryGetByBaseUrl(baseUrl, out var svc));
        Assert.Equal("llama", svc!.Name);
    }

    [Fact]
    public void TryGetByBaseUrl_RemoteEndpoint_ReturnsFalse()
    {
        Assert.False(Registry.TryGetByBaseUrl("https://api.example.com", out var svc));
        Assert.Null(svc);
    }

    [Fact]
    public void UsesGpu_MatchesComposeNvidiaReservations()
    {
        var gpu = new[] { "llama", "chatterbox", "chatterbox-turbo", "qwen3-tts", "qwen3-tts-base", "voxcpm2" };
        var cpu = new[] { "whisper", "minilm-l6", "mpnet-base-v2" };

        foreach (var name in gpu)
            Assert.True(Registry.GetByName(name).UsesGpu, $"{name} should be GPU");
        foreach (var name in cpu)
            Assert.False(Registry.GetByName(name).UsesGpu, $"{name} should be CPU");
    }

    [Fact]
    public void GetByName_Whisper_ReturnsWhisperEntry()
    {
        var svc = Registry.GetByName("whisper");
        Assert.Equal("read2me-whisper", svc.ContainerName);
        Assert.Equal("http://localhost:9000", svc.BaseUrl);
    }

    [Fact]
    public void GetByName_UnknownName_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Registry.GetByName("does-not-exist"));
    }

    // Mirrors the SupportsModelSwitch backfill predicate in migration
    // 20260718012345_AddSupportsModelSwitch: lowercase, strip a trailing '/', then match the two
    // llama host forms. Kept here beside the registry so the two stay consistent.
    private static bool BackfillPredicate(string baseUrl)
    {
        var normalized = baseUrl.ToLowerInvariant().TrimEnd('/');
        return normalized is "http://localhost:8080" or "http://127.0.0.1:8080";
    }

    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("http://LOCALHOST:8080", true)]
    [InlineData("http://localhost:8081", false)]
    [InlineData("http://localhost:8000", false)]
    [InlineData("https://api.example.com", false)]
    [InlineData("https://localhost:8080", false)]
    public void Backfill_SupportsModelSwitchPredicate_MatchesLlamaEndpoint(string baseUrl, bool expected)
    {
        Assert.Equal(expected, BackfillPredicate(baseUrl));

        // The predicate must agree with the registry: exactly the llama endpoint backfills true.
        var resolvesToLlama = Registry.TryGetByBaseUrl(baseUrl, out var svc) && svc!.Name == "llama";
        Assert.Equal(expected, resolvesToLlama);
    }

    [Fact]
    public void NativeHealthEndpoints_AreRegistered()
    {
        Assert.Equal("/health", Registry.GetByName("llama").HealthPath);
        Assert.Equal("/health", Registry.GetByName("whisper").HealthPath);
        Assert.Equal("/docs", Registry.GetByName("minilm-l6").HealthPath);
    }
}
