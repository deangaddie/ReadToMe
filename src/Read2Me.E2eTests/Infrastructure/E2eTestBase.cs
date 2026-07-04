using Microsoft.Playwright;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Fresh browser context + page per test (isolated Blazor circuit).
/// Interact only after <see cref="GotoAsync"/> returns — it waits for the
/// SignalR circuit, because clicks on prerendered static HTML are dropped.
/// </summary>
public abstract class E2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : IAsyncLifetime
{
    protected E2eAppFixture App => app;
    protected IPage Page { get; private set; } = null!;
    private IBrowserContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            BaseURL = app.BaseUrl,
        });
        // External font in _Host.cshtml — keep tests offline-deterministic.
        await _context.RouteAsync("https://fonts.googleapis.com/**", r => r.AbortAsync());
        Page = await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
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
