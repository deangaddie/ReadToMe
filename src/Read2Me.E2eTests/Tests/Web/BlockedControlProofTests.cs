using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #473: a control blocked with a reason is reached by Tab, says why to
/// assistive technology, and runs nothing.
/// </summary>
public class BlockedControlProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Reason = "Every paragraph has a speaker";
    private const string Viewing = "Viewing a past step. Restore it to change anything";

    private string Browser => Page.Context.Browser!.BrowserType.Name;

    private static void Log(string line) => TestContext.Current.TestOutputHelper!.WriteLine(line);

    // Needs a real browser: the tab order, the accessibility tree and real activation.
    [Fact]
    public async Task Tab_reaches_a_blocked_pipeline_action_which_says_why_and_runs_nothing()
    {
        await App.SeedNarrationBookAsync("blocked-proof", "Blocked Proof", ["It was a dark and stormy night."]);
        await GotoAppAsync("projects/blocked-proof");
        var action = Page.Locator("[data-action='attribute']");
        await Expect(action).ToHaveAttributeAsync("aria-disabled", "true");

        // Tab from the top of the page until the action has focus.
        var presses = 0;
        while (!await action.EvaluateAsync<bool>("el => el === document.activeElement"))
        {
            Assert.True(++presses < 80, "Tab never reached the blocked action");
            await Page.Keyboard.PressAsync("Tab");
        }
        var focused = Page.Locator(":focus");
        await Expect(focused).ToHaveRoleAsync(AriaRole.Button);
        await Expect(focused).ToHaveAccessibleNameAsync("Attribute unprocessed");
        await Expect(focused).ToHaveAccessibleDescriptionAsync(Reason);
        Log($"[{Browser}] focused after {presses} Tab presses");
        Log($"[{Browser}] aria snapshot: {await focused.AriaSnapshotAsync()}");
        await AssertInChromiumTree("Attribute unprocessed", Reason);

        // The tooltip shows from the control itself, on focus; the description is out of sight and
        // adds no scroll.
        await Expect(Page.Locator(".r2m-tooltip")).ToHaveTextAsync(Reason);
        await Expect(Page.Locator(".r2m-tooltip")).ToBeVisibleAsync();
        var hidden = Page.Locator($"#{await action.GetAttributeAsync("aria-describedby")}");
        Assert.True((await hidden.BoundingBoxAsync() ?? new()).Width <= 1);

        // Click (forced: Playwright itself waits out aria-disabled), Enter and Space run nothing.
        var writes = Writes();
        await action.ClickAsync(new() { Force = true });
        await action.PressAsync("Enter");
        await action.PressAsync("Space");
        await Page.WaitForTimeoutAsync(1000);
        Assert.Empty(writes);
        await Expect(Page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        Log($"[{Browser}] click, Enter and Space: no write request, no dialog");

        // The same observer does see the action run once its gate is open.
        await App.SeedThreeDialogParagraphProjectAsync("open-proof", "Open Proof", "A. Author");
        await GotoAppAsync("projects/open-proof");
        await Expect(action).Not.ToHaveAttributeAsync("aria-disabled", "true");
        await Expect(action).ToHaveAccessibleDescriptionAsync("");
        var after = Writes();
        await action.PressAsync("Enter");
        await Page.WaitForTimeoutAsync(1000);
        Assert.NotEmpty(after);
        Log($"[{Browser}] open gate, Enter: {string.Join(", ", after)}");
    }

    // Needs a real browser: a native checkbox, tree row and radio act (or not) as the engine says.
    [Fact]
    public async Task A_viewed_step_leaves_native_controls_unchanged_and_out_of_names()
    {
        await App.SeedProjectAsync("blocked-native", "Blocked Native", "A. Author");
        await GotoAppAsync("projects/blocked-native");
        await Page.EvaluateAsync("""
            async () => {
              await fetch('/api/projects/blocked-native/history');
              await fetch('/api/projects/blocked-native', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title: 'Renamed' }),
              });
            }
            """);
        await Page.GetByRole(AriaRole.Button, new() { Name = "History" }).ClickAsync();
        var steps = Page.Locator(".history-sheet__step");
        await Expect(steps).ToHaveCountAsync(2);
        await steps.Last.ClickAsync();

        var narratorOnly = Page.GetByRole(AriaRole.Switch, new() { Name = "Narrator only" });
        await Expect(narratorOnly).ToHaveAccessibleDescriptionAsync(Viewing);
        var writes = Writes();
        await narratorOnly.ClickAsync(new() { Force = true });
        await Page.Locator(".details__narrator-only").ClickAsync(new() { Position = new() { X = 80, Y = 8 }, Force = true });
        await narratorOnly.FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await Page.WaitForTimeoutAsync(1000);
        Assert.False(await narratorOnly.IsCheckedAsync());
        Assert.Empty(writes);
        Log($"[{Browser}] blocked switch: click, label click and Space left it unchecked, nothing written");

        await Page.Locator(".shell__nav a", new() { HasText = "Book" }).First.ClickAsync();
        var trigger = Page.Locator(".tree .r2m-node-menu__trigger").First;
        await Expect(trigger).ToHaveAttributeAsync("aria-disabled", "true");
        await Expect(trigger).ToHaveAttributeAsync("tabindex", "-1");
        var row = Page.GetByRole(AriaRole.Treeitem).First;
        await Expect(row).Not.ToHaveAccessibleNameAsync(new System.Text.RegularExpressions.Regex("Viewing a past step"));
        Log($"[{Browser}] tree row snapshot: {await row.AriaSnapshotAsync()}");
        await trigger.ClickAsync(new() { Force = true });
        await Expect(Page.GetByRole(AriaRole.Menu)).ToHaveCountAsync(0);

        // A popover invoker opens nothing.
        var actions = Page.Locator("[data-testid='book-actions']");
        await actions.ClickAsync(new() { Force = true });
        await actions.PressAsync("Enter");
        await Expect(Page.GetByRole(AriaRole.Menu)).ToHaveCountAsync(0);
        Log($"[{Browser}] blocked node menu trigger and Book actions popover: no menu opened");
    }

    /// <summary>The requests from here on that could change anything.</summary>
    private List<string> Writes()
    {
        var writes = new List<string>();
        Page.Request += (_, r) =>
        {
            if (r.Method != "GET" && !r.Url.Contains("/hubs/")) writes.Add($"{r.Method} {new Uri(r.Url).PathAndQuery}");
        };
        return writes;
    }

    /// <summary>
    /// Chromium only: the browser's own accessibility tree (CDP), beside Playwright's computed
    /// role, name and description above, which is all Firefox offers a test.
    /// </summary>
    private async Task AssertInChromiumTree(string name, string description)
    {
        if (Browser != "chromium") return;
        var session = await Page.Context.NewCDPSessionAsync(Page);
        var tree = await session.SendAsync("Accessibility.getFullAXTree");
        var node = tree!.Value.GetProperty("nodes").EnumerateArray().Single(n =>
            Value(n, "role") == "button" && Value(n, "name") == name);
        Log($"[chromium AX tree] role={Value(node, "role")} name='{Value(node, "name")}' description='{Value(node, "description")}' "
            + $"properties={string.Join(",", node.GetProperty("properties").EnumerateArray().Select(p => $"{p.GetProperty("name")}={p.GetProperty("value").GetProperty("value")}"))}");
        Assert.Equal(description, Value(node, "description"));

        static string? Value(JsonElement n, string property) =>
            n.TryGetProperty(property, out var p) && p.TryGetProperty("value", out var v) ? v.ToString() : null;
    }
}
