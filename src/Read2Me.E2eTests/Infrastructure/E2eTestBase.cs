using Microsoft.Playwright;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Fresh browser context + page per test (isolated Blazor circuit).
/// Interact only after <see cref="GotoAsync"/> returns — it waits for the
/// SignalR circuit, because clicks on prerendered static HTML are dropped.
///
/// On failure, saves a Playwright trace (.zip), a final screenshot, and the
/// session video under artifacts/&lt;test-name&gt;/ (gitignored — see repo
/// .gitignore's generic "artifacts/" rule). Passing tests keep nothing.
/// Open a trace with: npx playwright show-trace &lt;path&gt;
/// </summary>
public abstract class E2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : IAsyncLifetime
{
    protected E2eAppFixture App => app;
    protected IPage Page { get; private set; } = null!;
    private IBrowserContext _context = null!;
    private string _artifactsDir = "";

    public async ValueTask InitializeAsync()
    {
        // The app fixture is collection-shared and tests mutate its fakes (e.g.
        // DockerServiceControlsTests shuts the fake service down, which makes the
        // AI preflight dialog block every later "Add to ... queue" click). Restore
        // defaults so test order can't leak state.
        app.FakeControl.Status = Read2Me.Services.Health.AiServiceStatus.Ready;
        app.FakeAi.Reset();

        var testName = TestContext.Current.Test?.TestDisplayName ?? "unknown-test";
        var safeName = string.Concat(testName.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        _artifactsDir = Path.Combine(AppContext.BaseDirectory, "artifacts", safeName);
        Directory.CreateDirectory(_artifactsDir);

        _context = await pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            BaseURL = app.BaseUrl,
            RecordVideoDir = _artifactsDir,
            RecordVideoSize = new RecordVideoSize { Width = 1440, Height = 900 },
        });
        await _context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
        });
        // External font in _Host.cshtml — keep tests offline-deterministic.
        await _context.RouteAsync("https://fonts.googleapis.com/**", r => r.AbortAsync());
        Page = await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var failed = TestContext.Current.TestState?.Result == TestResult.Failed;

        if (failed)
        {
            try { await Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(_artifactsDir, "failure.png") }); }
            catch { /* page may already be closed/navigated away */ }

            await _context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine(_artifactsDir, "trace.zip"),
            });
            await _context.CloseAsync(); // flushes the video file to RecordVideoDir
            Console.WriteLine($"E2E artifacts (trace/screenshot/video) saved to: {_artifactsDir}");
        }
        else
        {
            await _context.Tracing.StopAsync(); // no Path => discarded
            await _context.CloseAsync(); // finalizes (and discards) the video file
            Directory.Delete(_artifactsDir, recursive: true);
        }
    }

    /// <summary>Navigates and waits for the Blazor Server circuit to be live.</summary>
    protected async Task GotoAsync(string path)
    {
        var wsTask = Page.WaitForWebSocketAsync(new PageWaitForWebSocketOptions { Timeout = 15_000 });
        await Page.GotoAsync(path);
        var ws = await wsTask;
        Assert.Contains("_blazor", ws.Url);
        // First interactive render follows the socket handshake; NetworkIdle covers it.
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    protected static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
