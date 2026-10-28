using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>Runs the Quick check once as the host starts, off the start-up path.</summary>
    public sealed class SetupCheckAtStart(ISetupCheckRunner runner, ILogger<SetupCheckAtStart> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var report = await runner.RunQuickCheckAsync(stoppingToken);
                var failed = report.Rows.Where(r => r.Result == SetupCheckResult.Failed).Select(r => r.Name).ToList();
                if (failed.Count > 0)
                    logger.LogWarning("Setup check: {Count} failed ({Checks})", failed.Count, string.Join(", ", failed));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The start-up Quick check failed to run");
            }
        }
    }
}
