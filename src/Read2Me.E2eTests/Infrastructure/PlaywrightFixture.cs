using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Read2Me.E2eTests.Infrastructure.PlaywrightFixture))]

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// One Playwright + browser instance for the whole test assembly. <c>R2M_E2E_BROWSER</c> picks
/// <c>chromium</c> (the default) or <c>firefox</c>; the suite runs once per browser, so a PR gate
/// invokes <c>dotnet test</c> twice. Angular classes only run under Chromium (ADR 0014).
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public const string BrowserVariable = "R2M_E2E_BROWSER";

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>The Playwright browser name this run launches: <c>chromium</c> or <c>firefox</c>.</summary>
    public string BrowserName { get; } = ResolveBrowserName();

    public bool IsFirefox => BrowserName == "firefox";

    private IPlaywright? _playwright;

    public async ValueTask InitializeAsync()
    {
        // No-op if already installed; downloads the browser on first run.
        var exit = Microsoft.Playwright.Program.Main(["install", BrowserName]);
        if (exit != 0)
            throw new InvalidOperationException($"playwright install {BrowserName} failed with exit code {exit}");

        _playwright = await Playwright.CreateAsync();
        var slowMo = float.TryParse(Environment.GetEnvironmentVariable("E2E_SLOWMO"), out var ms) ? ms : 0;
        var browserType = IsFirefox ? _playwright.Firefox : _playwright.Chromium;
        Browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
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

    private static string ResolveBrowserName()
    {
        var value = Environment.GetEnvironmentVariable(BrowserVariable)?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" or "chromium" => "chromium",
            "firefox" => "firefox",
            _ => throw new InvalidOperationException(
                $"{BrowserVariable}='{value}' is not supported; use 'chromium' (default) or 'firefox'."),
        };
    }
}
