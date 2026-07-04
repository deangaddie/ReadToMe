using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Health;

/// <summary>
/// Singleton catalogue of every Docker-hosted AI service the watchdog can manage.
/// Container names and ports mirror <c>Infra/docker-compose.yml</c>. A base URL that
/// matches no entry (e.g. a remote OpenAI-compatible endpoint) is treated as unmanaged.
/// </summary>
public sealed class DockerAiServiceRegistry
{
    private readonly IReadOnlyDictionary<string, DockerAiService> _byName;
    private readonly IReadOnlyDictionary<(string Scheme, string Host, int Port), DockerAiService> _byEndpoint;

    public DockerAiServiceRegistry()
    {
        var services = new[]
        {
            new DockerAiService("llama",            "read2me-llama",          "http://localhost:8080", "/health", LlamaWarmup("http://localhost:8080")),
            new DockerAiService("chatterbox",       "read2me-chatterbox",     "http://localhost:8000", "/docs"),
            new DockerAiService("chatterbox-turbo", "read2me-chatterbox-turbo", "http://localhost:8001", "/docs"),
            new DockerAiService("qwen3-tts",        "read2me-qwen3-tts",      "http://localhost:8100", "/docs"),
            new DockerAiService("qwen3-tts-base",   "read2me-qwen3-tts-base", "http://localhost:8101", "/docs"),
            new DockerAiService("voxcpm2",          "read2me-voxcpm2",        "http://localhost:8003", "/docs"),
            new DockerAiService("whisper",          "read2me-whisper",        "http://localhost:9000", "/docs"),
            new DockerAiService("whisper-cpu",      "read2me-whisper-cpu",    "http://localhost:9001", "/docs"),
            new DockerAiService("minilm-l6",        "read2me-minilm-l6",      "http://localhost:8200", "/docs", SimilarityWarmup("http://localhost:8200")),
            new DockerAiService("mpnet-base-v2",    "read2me-mpnet-base-v2",  "http://localhost:8201", "/docs", SimilarityWarmup("http://localhost:8201")),
        };

        var byName = new Dictionary<string, DockerAiService>(StringComparer.OrdinalIgnoreCase);
        var byEndpoint = new Dictionary<(string, string, int), DockerAiService>();
        foreach (var svc in services)
        {
            byName[svc.Name] = svc;
            var uri = new Uri(svc.BaseUrl);
            byEndpoint[(uri.Scheme, NormalizeHost(uri.Host), uri.Port)] = svc;
        }

        _byName = byName;
        _byEndpoint = byEndpoint;
        All = services;
    }

    /// <summary>Every registered service, for callers that build per-service maps (e.g. the gate map).</summary>
    public IReadOnlyCollection<DockerAiService> All { get; }

    /// <summary>
    /// Resolves a base URL to its managed service, matching on scheme/host/port and tolerating a
    /// trailing slash and <c>127.0.0.1</c> vs <c>localhost</c>. A remote URL simply misses.
    /// </summary>
    public bool TryGetByBaseUrl(string baseUrl, [NotNullWhen(true)] out DockerAiService? service)
    {
        service = null;
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return _byEndpoint.TryGetValue((uri.Scheme, NormalizeHost(uri.Host), uri.Port), out service);
    }

    /// <summary>Direct lookup by stable name. Throws <see cref="KeyNotFoundException"/> if unknown.</summary>
    public DockerAiService GetByName(string name)
    {
        if (_byName.TryGetValue(name, out var service))
        {
            return service;
        }

        throw new KeyNotFoundException($"No Docker AI service registered with name '{name}'.");
    }

    private static string NormalizeHost(string host) =>
        host is "127.0.0.1" ? "localhost" : host.ToLowerInvariant();

    /// <summary>
    /// llama warm-up: a one-token "hi" completion sent through the real <see cref="ILlmClient"/> with
    /// the user's active <see cref="LlmServerConfig"/> — the same model, API key and URL real traffic
    /// uses. The TurboQuant fork has no server-default model (a completion with no <c>model</c> field
    /// is rejected 400), and warming any other model would force a reload on the first real request,
    /// so we must send the configured model. If no active config targets this service (or it has no
    /// model), there is nothing safe to warm and health alone is treated as readiness.
    /// </summary>
    private Func<IServiceProvider, CancellationToken, Task> LlamaWarmup(string baseUrl)
    {
        return async (sp, ct) =>
        {
            var settings = sp.GetRequiredService<LlmSettingsService>();
            var active = await settings.GetActiveConfigAsync();
            if (active is null || string.IsNullOrWhiteSpace(active.Model) || !TargetsEndpoint(active.BaseUrl, baseUrl))
                return;

            var llm = sp.GetRequiredService<ILlmClient>();
            // Clone with max_tokens = 1: force the model load without generating a full response.
            var warm = new LlmServerConfig
            {
                ApiType = active.ApiType,
                BaseUrl = active.BaseUrl,
                ApiKey = active.ApiKey,
                Model = active.Model,
                MaxTokens = 1,
            };
            await foreach (var _ in llm.StreamChatAsync(warm, "hi", ct))
            {
                // Drain the stream; the load happens server-side as the first token is produced.
            }
        };
    }

    /// <summary>Similarity warm-up: two one-word texts, forcing the embedding model to load.</summary>
    private static Func<IServiceProvider, CancellationToken, Task> SimilarityWarmup(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/') + "/similarity";
        return async (sp, ct) =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var response = await http.PostAsJsonAsync(url, new { text1 = "hi", text2 = "hello" }, ct);
            response.EnsureSuccessStatusCode();
        };
    }

    /// <summary>
    /// True when the active config's base URL resolves to the same managed endpoint as
    /// <paramref name="serviceBaseUrl"/> (tolerating trailing slash and 127.0.0.1 vs localhost).
    /// </summary>
    private static bool TargetsEndpoint(string configBaseUrl, string serviceBaseUrl)
    {
        if (!Uri.TryCreate(configBaseUrl, UriKind.Absolute, out var a) ||
            !Uri.TryCreate(serviceBaseUrl, UriKind.Absolute, out var b))
        {
            return false;
        }

        return a.Scheme == b.Scheme
            && NormalizeHost(a.Host) == NormalizeHost(b.Host)
            && a.Port == b.Port;
    }
}
