using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Read2Me.Services.Health;

/// <summary>
/// <see cref="IAiServiceProbe"/> over <see cref="IHttpClientFactory"/> — keeps the E2E fake-AI
/// seam working. Health polling and warm-up bounds come from <see cref="AiWatchdogOptions"/>.
/// Warm-up runs inside a fresh DI scope so a config-driven warm-up can resolve scoped services
/// (e.g. the active LLM config and <c>ILlmClient</c>).
/// </summary>
public sealed class AiServiceProbe(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<AiWatchdogOptions> options,
    ILogger<AiServiceProbe> logger) : IAiServiceProbe
{
    private AiWatchdogOptions Options => options.Value;

    public async Task<bool> WaitUntilHealthyAsync(DockerAiService service, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        var url = service.BaseUrl.TrimEnd('/') + service.HealthPath;
        var interval = TimeSpan.FromSeconds(Options.HealthPollIntervalSeconds);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Options.HealthPollTimeoutSeconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Connection refused / request timeout while the server comes up — just "not yet".
                logger.LogDebug(ex, "Health poll for {Service} not ready yet", service.Name);
            }

            if (DateTimeOffset.UtcNow + interval > deadline)
            {
                logger.LogWarning("Health poll for {Service} timed out after {Timeout}s",
                    service.Name, Options.HealthPollTimeoutSeconds);
                return false;
            }

            await Task.Delay(interval, ct);
        }
    }

    public async Task<bool> IsHealthyAsync(DockerAiService service, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        var url = service.BaseUrl.TrimEnd('/') + service.HealthPath;
        try
        {
            var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Connection refused / 5xx / timeout — the server is up as a container but not answering yet.
            logger.LogDebug(ex, "Single health probe for {Service} failed", service.Name);
            return false;
        }
    }

    public async Task<bool> WarmupAsync(DockerAiService service, CancellationToken ct)
    {
        if (service.Warmup is null)
        {
            // Health alone is this service's readiness.
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Options.WarmupTimeoutSeconds));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await service.Warmup(scope.ServiceProvider, timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Warm-up for {Service} failed", service.Name);
            return false;
        }
    }
}
