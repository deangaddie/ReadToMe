using Microsoft.Playwright;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Base for tests that drive a web app on the in-proc host: a fresh browser context and page per
/// test, over the collection's shared fixture, fakes and seeders. A class declares the app it
/// drives through <see cref="WebApp"/> (Angular at <c>/app</c> by default, or the native app at
/// <c>/app2</c>); <see cref="GotoAppAsync"/> takes paths relative to that app's prefix.
/// <para>
/// The host's web root is a throwaway directory, so the bundle the app's build emitted into
/// <c>src/Read2Me.App/wwwroot/&lt;app&gt;</c> is copied in before every test (other tests in the
/// collection stage and remove stub bundles there). Without a built bundle the test is skipped with
/// the command that produces one, never failed: the .NET build touches neither front end.
/// </para>
/// <para>
/// The browser comes from <see cref="PlaywrightFixture"/> (<c>R2M_E2E_BROWSER</c>). Native classes
/// run in Chromium and Firefox; Angular classes are skipped under Firefox.
/// </para>
/// <para>
/// On failure, saves a Playwright trace (.zip), a final screenshot, and the session video under
/// artifacts/&lt;test-name&gt;/ (gitignored — see repo .gitignore's generic "artifacts/" rule).
/// Passing tests keep nothing. Open a trace with: npx playwright show-trace &lt;path&gt;
/// </para>
/// </summary>
public abstract class E2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : IAsyncLifetime
{
    /// <summary>
    /// Where each app's build puts its bundle (<c>src/Read2Me.App/wwwroot/app</c> or <c>app2</c>),
    /// found from the test binary upwards; null when that app has not been built.
    /// </summary>
    public static string? BuiltBundleDir(WebApp webApp) => FindBuiltBundle(webApp);

    /// <summary>The app this class drives. Native test classes override it with <see cref="WebApp.Native"/>.</summary>
    protected virtual WebApp WebApp => WebApp.Angular;

    protected E2eAppFixture App => app;
    protected IPage Page { get; private set; } = null!;
    private IBrowserContext? _context;
    private string _artifactsDir = "";

    public async ValueTask InitializeAsync()
    {
        // Before any seeding or browser work: an Angular class has nothing to do under Firefox.
        if (WebApp == WebApp.Angular && pw.IsFirefox)
            Assert.Skip($"Angular classes run in Chromium only; unset {PlaywrightFixture.BrowserVariable} or set it to chromium.");

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
    /// Stages the built bundle for <see cref="WebApp"/> and navigates to an app-relative path
    /// (<c>"projects/x/book"</c>), returning once the live hub socket has opened and the shell has
    /// rendered, the point after which receipts drive the screen. Skips the test
    /// when that app has no built bundle (the Angular-under-Firefox skip happens in <see cref="InitializeAsync"/>).
    /// </summary>
    protected async Task GotoAppAsync(string path)
    {
        var bundle = BuiltBundleDir(WebApp);
        if (bundle is null)
            Assert.Skip($"No {WebApp} bundle at src/Read2Me.App/wwwroot/{WebApp.BundleFolder()} — run {WebApp.BuildCommand()} first.");

        StageBundle(bundle, Path.Combine(App.WebRootDir, WebApp.BundleFolder()));

        // Both apps open the live hub on boot; receipts drive the screen from then on.
        var hub = Page.WaitForWebSocketAsync(new PageWaitForWebSocketOptions { Timeout = 15_000 });
        await Page.GotoAsync(AppPath(path));
        Assert.Contains("/hubs/live", (await hub).Url);
        await Expect(Page.Locator("app-root *").First).ToBeVisibleAsync();
    }

    /// <summary>
    /// The host path for an app-relative path: <c>"projects/x"</c> becomes <c>/app/projects/x</c> or
    /// <c>/app2/projects/x</c>, and <c>""</c> the app root. For direct <c>Page.GotoAsync</c> calls
    /// and URL assertions. A <c>/</c>-prefixed path is a mistake and throws.
    /// </summary>
    protected string AppPath(string relativePath)
    {
        if (relativePath.StartsWith('/'))
            throw new ArgumentException($"App paths are relative to the app prefix; got '{relativePath}'.", nameof(relativePath));
        return $"{WebApp.Prefix()}/{relativePath}";
    }

    protected static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

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

    private static string? FindBuiltBundle(WebApp webApp)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Read2Me.slnx"))) continue;
            var bundle = Path.Combine(dir.FullName, "Read2Me.App", "wwwroot", webApp.BundleFolder());
            return File.Exists(Path.Combine(bundle, "index.html")) ? bundle : null;
        }
        return null;
    }
}
