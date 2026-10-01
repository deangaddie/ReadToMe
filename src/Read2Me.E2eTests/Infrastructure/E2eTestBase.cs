using Microsoft.Playwright;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Base for tests that drive the Angular web app (<c>/app/...</c>) on the in-proc host: a fresh
/// browser context and page per test, over the collection's shared fixture, fakes and seeders.
/// <para>
/// The host's web root is a throwaway directory, so the bundle <c>npm run build</c> emitted into
/// <c>src/Read2Me.App/wwwroot/app</c> is copied in before every test (other tests in the collection
/// stage and remove stub bundles there). Without a built bundle the test is skipped with the
/// command that produces one, never failed: the .NET build does not touch the web project.
/// </para>
/// <para>
/// On failure, saves a Playwright trace (.zip), a final screenshot, and the session video under
/// artifacts/&lt;test-name&gt;/ (gitignored — see repo .gitignore's generic "artifacts/" rule).
/// Passing tests keep nothing. Open a trace with: npx playwright show-trace &lt;path&gt;
/// </para>
/// </summary>
public abstract class E2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : IAsyncLifetime
{
    /// <summary>Where <c>npm run build</c> puts the bundle, found from the test binary upwards.</summary>
    public static string? BuiltBundleDir { get; } = FindBuiltBundle();

    protected E2eAppFixture App => app;
    protected IPage Page { get; private set; } = null!;
    private IBrowserContext _context = null!;
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
    /// Stages the built bundle and navigates to an <c>/app/...</c> path, returning once the Angular
    /// shell has rendered and the live hub is connected — the point after which receipts drive the
    /// screen. Skips the test when no bundle has been built.
    /// </summary>
    protected async Task GotoAppAsync(string path)
    {
        if (BuiltBundleDir is null)
            Assert.Skip("No Angular bundle at src/Read2Me.App/wwwroot/app — run `npm run build` in src/Read2Me.Web first.");

        StageBundle(BuiltBundleDir, Path.Combine(App.WebRootDir, "app"));

        var hub = Page.WaitForWebSocketAsync(new PageWaitForWebSocketOptions { Timeout = 15_000 });
        await Page.GotoAsync(path);
        Assert.Contains("/hubs/live", (await hub).Url);
        await Expect(Page.Locator("app-root *").First).ToBeVisibleAsync();
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

    private static string? FindBuiltBundle()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Read2Me.slnx"))) continue;
            var bundle = Path.Combine(dir.FullName, "Read2Me.App", "wwwroot", "app");
            return File.Exists(Path.Combine(bundle, "index.html")) ? bundle : null;
        }
        return null;
    }
}
