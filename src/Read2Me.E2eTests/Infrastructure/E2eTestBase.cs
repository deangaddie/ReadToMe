using Microsoft.Playwright;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Base for tests that drive the web app on the in-proc host: a fresh browser context and page per
/// test, over the collection's shared fixture, fakes and seeders. <see cref="GotoAppAsync"/> takes
/// paths relative to the app's prefix, <c>/app</c>.
/// <para>
/// The host's web root is a throwaway directory, so the bundle the web build emitted into
/// <c>src/Read2Me.App/wwwroot/app</c> is copied in before every test (other tests in the
/// collection stage and remove stub bundles there). Without a built bundle the test is skipped with
/// the command that produces one, never failed: the .NET build does not touch the front end.
/// </para>
/// <para>
/// The browser comes from <see cref="PlaywrightFixture"/> (<c>R2M_E2E_BROWSER</c>); every class
/// runs in Chromium and Firefox.
/// </para>
/// <para>
/// On failure, saves a Playwright trace (.zip), a final screenshot, and the session video under
/// artifacts/&lt;test-name&gt;/ (gitignored — see repo .gitignore's generic "artifacts/" rule).
/// Passing tests keep nothing. Open a trace with: npx playwright show-trace &lt;path&gt;
/// </para>
/// </summary>
public abstract class E2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : IAsyncLifetime
{
    /// <summary>The host prefix the web app is served under, and its bundle folder in the web root.</summary>
    private const string AppPrefix = "/app";
    private const string BundleFolder = "app";

    protected E2eAppFixture App => app;
    protected IPage Page { get; private set; } = null!;
    private IBrowserContext? _context;
    private string _artifactsDir = "";

    public async ValueTask InitializeAsync()
    {
        // The app fixture is collection-shared and tests mutate its fakes (e.g. shutting the fake
        // service down makes the AI pre-flight sheet block every later queue click, and leaves the
        // shutdown in the op log a later test asserts on). Restore defaults so test order can't
        // leak state.
        app.FakeControl.Reset();
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
        Page = await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is null) return; // skipped in InitializeAsync before a context existed
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

    /// <summary>
    /// Stages the built bundle and navigates to an app-relative path (<c>"projects/x/book"</c>),
    /// returning once the shell reports the live hub connected (<c>data-live-state</c>) and has
    /// rendered, the point after which receipts drive the screen. It waits on the app's own state,
    /// not a WebSocket event, so a transport fallback or a retried start still counts. Skips the
    /// test when the app has no built bundle.
    /// </summary>
    protected async Task GotoAppAsync(string path)
    {
        var bundle = FindBuiltBundle();
        if (bundle is null)
            Assert.Skip($"No web bundle at src/Read2Me.App/wwwroot/{BundleFolder} — run `pwsh scripts/build-web.ps1` (or `bun run build` in src/Read2Me.Web) first.");

        StageBundle(bundle, Path.Combine(App.WebRootDir, BundleFolder));

        // The app opens the live hub on boot; receipts drive the screen from then on.
        await Page.GotoAsync(AppPath(path));
        await Expect(Page.Locator("app-root[data-live-state='connected']")).ToBeAttachedAsync(new() { Timeout = 15_000 });
        await Expect(Page.Locator("app-root *").First).ToBeVisibleAsync();
    }

    /// <summary>
    /// The host path for an app-relative path: <c>"projects/x"</c> becomes <c>/app/projects/x</c>,
    /// and <c>""</c> the app root. For direct <c>Page.GotoAsync</c> calls and URL assertions. A
    /// <c>/</c>-prefixed path is a mistake and throws.
    /// </summary>
    protected static string AppPath(string relativePath)
    {
        if (relativePath.StartsWith('/'))
            throw new ArgumentException($"App paths are relative to the app prefix; got '{relativePath}'.", nameof(relativePath));
        return $"{AppPrefix}/{relativePath}";
    }

    protected static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

    /// <summary>
    /// Clicks a busy-gated Save and returns once the write has landed. The button disables on the
    /// click, before the request is stored, so "disabled again" alone passes too early. The host's
    /// <c>aria-busy</c> is set no later than the render that disables the button, so it clearing
    /// after that means the save finished.
    /// </summary>
    protected static async Task SaveAndSettleAsync(ILocator host, ILocator save)
    {
        await save.ClickAsync();
        await Expect(save).ToBeDisabledAsync();
        await Expect(host).Not.ToHaveAttributeAsync("aria-busy", "true");
    }

    private static void StageBundle(string source, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static string? FindBuiltBundle()
    {
        var bundle = Path.Combine(SourceTree.Root, "Read2Me.App", "wwwroot", BundleFolder);
        return File.Exists(Path.Combine(bundle, "index.html")) ? bundle : null;
    }
}
