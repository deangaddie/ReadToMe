using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Exceptions;
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
            new DockerAiService("llama",            "read2me-llama",          "http://localhost:8080", "/health", LlamaWarmup(), UsesGpu: true),
            new DockerAiService("chatterbox",       "read2me-chatterbox",     "http://localhost:8000", "/docs", UsesGpu: true),
            new DockerAiService("chatterbox-turbo", "read2me-chatterbox-turbo", "http://localhost:8001", "/docs", UsesGpu: true),
            new DockerAiService("qwen3-tts",        "read2me-qwen3-tts",      "http://localhost:8100", "/docs", UsesGpu: true),
            new DockerAiService("qwen3-tts-base",   "read2me-qwen3-tts-base", "http://localhost:8101", "/docs", UsesGpu: true),
            new DockerAiService("voxcpm2",          "read2me-voxcpm2",        "http://localhost:8003", "/docs", UsesGpu: true),
            new DockerAiService("whisper",          "read2me-whisper",        "http://localhost:9000", "/health"),
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
    /// llama warm-up: routes the user's active <see cref="Read2Me.AppData.Entities.LlmServerConfig"/>
    /// through the same <see cref="IModelLoadGate"/> real requests use — the single model-load path in
    /// the app. The gate no-ops when the config is not switchable or the target model is already
    /// loaded, so a non-switchable active config (or none) means there is nothing safe to warm and
    /// health alone is treated as readiness (matching the old "nothing safe to warm" branch).
    /// A <see cref="ModelStillLoadingException"/> here is soft/retryable: the load is progressing
    /// out-of-band (the gate's detached trigger runs past the budget) and the real request will wait
    /// on the gate, so we log "model still loading" and return without failing — never a watchdog trip
    /// or a hard preflight failure.
    /// </summary>
    private static Func<IServiceProvider, CancellationToken, Task> LlamaWarmup()
    {
        return async (sp, ct) =>
        {
            var settings = sp.GetRequiredService<LlmSettingsService>();
            var active = await settings.GetActiveConfigAsync();
            if (active is null)
                return;

            try
            {
                await sp.GetRequiredService<IModelLoadGate>().EnsureModelLoadedAsync(active, ct);
            }
            catch (ModelStillLoadingException ex)
            {
                sp.GetService<ILogger<DockerAiServiceRegistry>>()?.LogInformation(
                    ex, "llama warm-up: model still loading on {BaseUrl}; treating health as readiness", ex.BaseUrl);
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
}
