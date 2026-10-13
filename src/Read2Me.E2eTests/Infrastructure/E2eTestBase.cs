using Microsoft.Playwright;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Base for tests that drive the web app on the in-proc host: a fresh browser context and page per
/// test, over the class's own host (<see cref="E2eAppFixture"/> as a class fixture), fakes and
/// seeders. Classes run in parallel, each on its own host; the tests inside a class run in order
/// and share it. <see cref="GotoAppAsync"/> takes paths relative to the app's prefix, <c>/app</c>.
/// <para>
/// The host's web root is a throwaway directory, so the bundle the web build emitted into
/// <c>src/Read2Me.App/wwwroot/app</c> is copied in before every navigation. Each host has its own
/// web root, so no other class can disturb it; the copy is kept as a cheap guarantee. Without a
/// built bundle the test is skipped with the command that produces one, never failed: the .NET
/// build does not touch the front end.
/// </para>
/// <para>
/// The browser comes from <see cref="PlaywrightFixture"/> (<c>R2M_E2E_BROWSER</c>); every class
/// runs in Chromium and Firefox.
/// </para>
/// <para>
/// On failure, saves a Playwright trace (.zip) and a final screenshot under
/// artifacts/&lt;test-name&gt;/ (gitignored — see repo .gitignore's generic "artifacts/" rule).
/// Passing tests keep nothing. Open a trace with: npx playwright show-trace &lt;path&gt;
/// </para>
/// </summary>
public abstract class E2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : IClassFixture<E2eAppFixture>, IAsyncLifetime
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
        // The host is shared by every test in this class and tests mutate its fakes (e.g. shutting
        // the fake service down makes the AI pre-flight sheet block every later queue click, and
        // leaves the shutdown in the op log a later test asserts on). Restore defaults so test
        // order inside the class can't leak state.
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
            await _context.CloseAsync();
            Console.WriteLine($"E2E artifacts (trace/screenshot) saved to: {_artifactsDir}");
        }
        else
        {
            await _context.Tracing.StopAsync(); // no Path => discarded
            await _context.CloseAsync();
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

    /// <summary>The panel sits just below the trigger (block-end) and overlaps it horizontally.</summary>
    protected static void AssertAnchored(LocatorBoundingBoxResult anchor, LocatorBoundingBoxResult panel)
    {
        Assert.InRange(panel.Y - (anchor.Y + anchor.Height), 0, 12);
        Assert.True(panel.X < anchor.X + anchor.Width && panel.X + panel.Width > anchor.X,
            $"panel x {panel.X}..{panel.X + panel.Width} does not overlap trigger x {anchor.X}..{anchor.X + anchor.Width}");
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
