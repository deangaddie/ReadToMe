using System;
using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// Static description of a single Docker-hosted AI service the watchdog can manage.
/// One instance per compose service; created only in <see cref="DockerAiServiceRegistry"/>.
/// </summary>
/// <param name="Name">Stable identity, e.g. "llama", "chatterbox", "whisper".</param>
/// <param name="ContainerName">Docker container name, e.g. "read2me-llama".</param>
/// <param name="BaseUrl">Root URL the app reaches the service on, e.g. http://localhost:8080.</param>
/// <param name="HealthPath">
/// Cheap GET that answers once the HTTP server is up (typically /health; some FastAPI services use /docs).
/// </param>
/// <param name="Warmup">
/// Optional minimal real request that forces the model to load. Receives a DI scope's
/// <see cref="IServiceProvider"/> so a config-driven warm-up (e.g. llama, which must send the
/// user's configured model) can resolve scoped services. Null when health alone is readiness.
/// </param>
/// <param name="UsesGpu">
/// True when the container holds GPU VRAM while running (compose nvidia reservation). The single
/// RTX 3070 fits roughly one model at a time, so pre-flight offers to stop running GPU services
/// a task does not need.
/// </param>
public sealed record DockerAiService(
    string Name,
    string ContainerName,
    string BaseUrl,
    string HealthPath,
    Func<IServiceProvider, CancellationToken, Task>? Warmup = null,
    bool UsesGpu = false);
