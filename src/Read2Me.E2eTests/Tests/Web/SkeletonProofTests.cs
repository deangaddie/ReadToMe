using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #478: while a page loads, its skeleton is one progressbar named by what is
/// loading, and none of the skeleton's shapes reach the accessibility tree.
/// </summary>
public class SkeletonProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    /// <summary>Holds every request matching <paramref name="glob"/> until the returned gate opens.</summary>
    private async Task<TaskCompletionSource> HoldAsync(string glob)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync(glob, async route =>
        {
            await gate.Task;
            await route.ContinueAsync();
        });
        return gate;
    }

    private async Task AssertOnlyNamedProgressbarAsync(string name)
    {
        var bar = Page.GetByRole(AriaRole.Progressbar, new() { Name = name });
        await Expect(bar).ToBeVisibleAsync();
        await Expect(bar).ToHaveCountAsync(1);
        // The bar's whole subtree in the accessibility tree: the name, no shapes, no text.
        var snapshot = await bar.AriaSnapshotAsync();
        Console.WriteLine($"[{name}] {snapshot}");
        Assert.Equal($"- progressbar \"{name}\"", snapshot.Trim());
    }

    [Fact]
    public async Task The_chapter_skeleton_is_a_progressbar_named_Loading_chapter()
    {
        await App.SeedMultiChapterProjectAsync("skeleton-book", "Skeleton Book", "A. Author", chapters: 1, characterName: "Watson");
        var gate = await HoldAsync("**/api/projects/skeleton-book/book");

        var go = GotoAppAsync("projects/skeleton-book/book");
        await AssertOnlyNamedProgressbarAsync("Loading chapter");
        gate.SetResult();
        await go;
        await Expect(Page.GetByRole(AriaRole.Progressbar, new() { Name = "Loading chapter" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_cast_skeleton_is_a_progressbar_named_Loading_characters()
    {
        await App.SeedMultiChapterProjectAsync("skeleton-cast", "Skeleton Cast", "A. Author", chapters: 1, characterName: "Watson");
        var gate = await HoldAsync("**/api/projects/skeleton-cast/characters/summary");

        var go = GotoAppAsync("projects/skeleton-cast/cast");
        await AssertOnlyNamedProgressbarAsync("Loading characters");
        gate.SetResult();
        await go;
        await Expect(Page.GetByRole(AriaRole.Progressbar, new() { Name = "Loading characters" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_projects_skeleton_hides_its_card_shapes()
    {
        var gate = await HoldAsync("**/api/projects");

        var go = GotoAppAsync("projects");
        await AssertOnlyNamedProgressbarAsync("Loading projects");
        gate.SetResult();
        await go;
        await Expect(Page.GetByRole(AriaRole.Progressbar, new() { Name = "Loading projects" })).ToHaveCountAsync(0);
    }
}
