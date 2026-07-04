using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Read2Me.E2eTests.Infrastructure.PlaywrightFixture))]

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>One Playwright + Chromium instance for the whole test assembly.</summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IBrowser Browser { get; private set; } = null!;
    private IPlaywright? _playwright;

    public async ValueTask InitializeAsync()
    {
        // No-op if already installed; downloads Chromium on first run.
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0)
            throw new InvalidOperationException($"playwright install chromium failed with exit code {exit}");

        _playwright = await Playwright.CreateAsync();
        var slowMo = float.TryParse(Environment.GetEnvironmentVariable("E2E_SLOWMO"), out var ms) ? ms : 0;
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("E2E_HEADED") != "1",
            SlowMo = slowMo,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser != null) await Browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
