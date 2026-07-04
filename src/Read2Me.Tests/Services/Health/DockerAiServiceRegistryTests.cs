using System.Collections.Generic;
using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class DockerAiServiceRegistryTests
{
    private static readonly DockerAiServiceRegistry Registry = new();

    [Fact]
    public void ContainsAllTenComposeServices()
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
            ("whisper-cpu",      "read2me-whisper-cpu",    9001),
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

    [Fact]
    public void LlamaUsesHealthEndpoint_FastApiServicesUseDocs()
    {
        Assert.Equal("/health", Registry.GetByName("llama").HealthPath);
        Assert.Equal("/docs", Registry.GetByName("whisper").HealthPath);
        Assert.Equal("/docs", Registry.GetByName("minilm-l6").HealthPath);
    }
}
