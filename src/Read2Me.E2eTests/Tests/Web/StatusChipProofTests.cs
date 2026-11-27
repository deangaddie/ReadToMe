using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #474: no status chip is a live region.</summary>
public class StatusChipProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    /// <summary>The seeded LLM config's base URL, which the fake resolves to a service of the same name.</summary>
    private const string Llm = "http://fake-llm";

    private const string InLiveRegion =
        "el => !!el.closest('[role=status],[role=alert],[role=log],[aria-live],output')";

    private string Browser => Page.Context.Browser!.BrowserType.Name;

    private static void Log(string line) => TestContext.Current.TestOutputHelper!.WriteLine(line);

    // Needs a real browser: the bundle rendered in each engine, read with Playwright's ariaSnapshot.
    [Fact]
    public async Task The_projects_page_has_no_status_role_among_its_chips()
    {
        // The projects page only shows a chip for a failed read, so fail it.
        await Page.RouteAsync("**/api/projects", route => route.FulfillAsync(new()
        {
            Status = 500,
            ContentType = "application/problem+json",
            Body = """{"title":"Proof failure","status":500}""",
        }));
        await GotoAppAsync("projects");

        await AssertNoStatusAmongChips("projects page", Page.Locator("r2m-projects-page"));
    }

    // Needs a real browser: the bundle rendered in each engine, read with Playwright's ariaSnapshot.
    [Fact]
    public async Task The_pipeline_and_the_preflight_sheet_have_no_status_role_among_their_chips()
    {
        await App.SeedThreeDialogParagraphProjectAsync("chip-proof", "Chip Proof", "A. Author");
        App.FakeControl.StatusByName[Llm] = AiServiceStatus.Stopped;
        try
        {
            await GotoAppAsync("projects/chip-proof");
            await AssertNoStatusAmongChips("pipeline", Page.Locator(".r2m-pipeline"));

            await Page.Locator("[data-action='attribute']").ClickAsync();
            var sheet = Page.GetByRole(AriaRole.Dialog);
            await Expect(sheet.GetByRole(AriaRole.Button, new() { Name = "Start services" })).ToBeVisibleAsync();
            await AssertNoStatusAmongChips("preflight sheet", sheet);
        }
        finally
        {
            App.FakeControl.Reset();
        }
    }

    private async Task AssertNoStatusAmongChips(string where, ILocator scope)
    {
        var chips = scope.Locator(".r2m-status-chip");
        await Expect(chips.First).ToBeVisibleAsync();
        var count = await chips.CountAsync();

        var snapshot = await scope.AriaSnapshotAsync();
        Log($"[{Browser}] {where}: {count} chip(s); aria snapshot:\n{snapshot}\n");
        Assert.DoesNotContain("- status", snapshot);
        await Expect(scope.GetByRole(AriaRole.Status)).ToHaveCountAsync(0);

        // Nor does any chip sit inside a live region further up.
        for (var i = 0; i < count; i++)
            Assert.False(await chips.Nth(i).EvaluateAsync<bool>(InLiveRegion));

        // Control: all three checks do see the role when a chip carries it.
        await chips.First.EvaluateAsync("el => el.setAttribute('role', 'status')");
        Assert.Contains("- status", await scope.AriaSnapshotAsync());
        await Expect(scope.GetByRole(AriaRole.Status)).ToHaveCountAsync(1);
        Assert.True(await chips.First.EvaluateAsync<bool>(InLiveRegion));
        await chips.First.EvaluateAsync("el => el.removeAttribute('role')");
    }
}
