using Microsoft.Playwright;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Base for tests that drive the Angular web app (<c>/app/...</c>) on the in-proc host — the same
/// fixture, fakes and seeders the Blazor tests use, one Playwright runner for both UIs (spec D11).
/// <para>
/// The host's web root is a throwaway directory, so the bundle <c>npm run build</c> emitted into
/// <c>src/Read2Me.App/wwwroot/app</c> is copied in before every test (other tests in the collection
/// stage and remove stub bundles there). Without a built bundle the test is skipped with the
/// command that produces one, never failed: the .NET build does not touch the web project.
/// </para>
/// </summary>
public abstract class WebE2eTestBase(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    /// <summary>Where <c>npm run build</c> puts the bundle, found from the test binary upwards.</summary>
    public static string? BuiltBundleDir { get; } = FindBuiltBundle();

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
