using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #465, in the real engine: a toast raised over a modal dialog is mounted in it,
/// announced on it, in the accessibility tree and clickable; it goes back to the body when the
/// dialog closes; a toast under focus or the pointer outlives its time and runs out afterwards; and
/// dismissing a focused toast hands focus on. Needs the temporary hooks in <c>main.ts</c>.
/// </summary>
public class ToastProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Recorder = """
        window.__calls = [];
        for (const proto of [Document.prototype, Element.prototype]) {
          const real = proto.ariaNotify;
          if (!real) continue;
          proto.ariaNotify = function (text, options) {
            window.__calls.push(`${this.nodeName} | ${text} | ${options?.priority}`);
            return real.call(this, text, options);
          };
        }
        """;

    private string Engine => Page.Context.Browser!.BrowserType.Name;
    private ILocator Stack => Page.Locator(".r2m-toasts");
    private ILocator Toasts => Page.Locator(".r2m-toast");

    private Task<string> StackHomeAsync() =>
        Stack.EvaluateAsync<string>("el => `${el.parentElement.nodeName} open=${el.matches(':popover-open')}`");

    private static Task<bool> HasFocusAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("el => document.activeElement === el");

    private void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(
        $"{Page.Context.Browser!.BrowserType.Name} {Page.Context.Browser.Version}: {line}");

    private async Task FailUploadsAsync() =>
        await Page.RouteAsync("**/api/projects", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            await route.FulfillAsync(new()
            {
                Status = 500,
                ContentType = "application/problem+json",
                Body = """{"title":"Upload failed","status":500,"detail":"The book could not be read."}""",
            });
        });

    // Needs a real browser: inertness, the top layer and hit testing exist only in a real engine.
    [Fact]
    public async Task A_failed_upload_toast_is_exposed_and_clickable_over_the_new_project_dialog()
    {
        await Page.AddInitScriptAsync(Recorder);
        await GotoAppAsync("projects");
        await FailUploadsAsync();

        await Page.Locator(".projects__new").First.ClickAsync();
        var dialog = Page.Locator("dialog[open]");
        await dialog.Locator("input[name=bookTitle]").FillAsync("Paul Clifford");
        await dialog.Locator("input[name=author]").FillAsync("Edward Bulwer-Lytton");
        await dialog.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "clifford.txt",
            MimeType = "text/plain",
            Buffer = "It was a dark and stormy night."u8.ToArray(),
        });
        await dialog.Locator(".new-project__create").ClickAsync();
        // The dialog shows its own upload failure inline; it raises no toast.
        await Expect(dialog.Locator(".new-project__submit-error")).ToContainTextAsync("The book could not be read.");
        await Expect(Toasts).ToHaveCountAsync(0);

        // The same real call left unhandled goes through main.ts to toast.problem().
        await Page.EvaluateAsync("() => window.__uncaughtCreate()");
        var toast = dialog.Locator(".r2m-toast--error");
        await Expect(toast).ToContainTextAsync("The book could not be read.");
        Assert.Equal("DIALOG open=true", await StackHomeAsync());
        Assert.Equal("DIALOG | The book could not be read. | high",
            await Page.EvaluateAsync<string>("() => window.__calls.at(-1)"));

        var snapshot = await dialog.AriaSnapshotAsync();
        Log("dialog aria snapshot\n" + snapshot);
        Assert.Contains("region \"Notifications\"", snapshot);
        Assert.Contains("paragraph: The book could not be read.", snapshot);
        Assert.Contains("button \"Dismiss\"", snapshot);
        if (Engine == "chromium") await AssertInChromiumTreeAsync();

        // An inert element cannot take focus, and a real click would not reach it.
        var dismiss = toast.GetByRole(AriaRole.Button, new() { Name = "Dismiss" });
        await dismiss.FocusAsync();
        Assert.True(await HasFocusAsync(dismiss), "Dismiss should take focus: the toast is not inert");
        await dismiss.ClickAsync(new() { Timeout = 3000 });
        await Expect(Toasts).ToHaveCountAsync(0);
        await Expect(dialog).ToBeVisibleAsync();

        // The stack follows the dialog out, still showing and clickable.
        await Page.EvaluateAsync("() => window.__uncaughtCreate()");
        await Expect(toast).ToBeVisibleAsync();
        await dialog.Locator("input[name=author]").FocusAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.Locator("dialog")).ToHaveCountAsync(0);
        Assert.Equal("BODY open=true", await StackHomeAsync());
        await Page.Locator(".r2m-toast__dismiss").ClickAsync(new() { Timeout = 3000 });
        await Expect(Toasts).ToHaveCountAsync(0);
        Log("toast usable over the dialog and after it");
    }

    /// <summary>Chromium's own accessibility tree, which leaves inert content out.</summary>
    private async Task AssertInChromiumTreeAsync()
    {
        var cdp = await Page.Context.NewCDPSessionAsync(Page);
        var tree = (await cdp.SendAsync("Accessibility.getFullAXTree"))!.Value;
        var exposed = tree.GetProperty("nodes").EnumerateArray()
            .Where(n => !n.GetProperty("ignored").GetBoolean())
            .Select(n => $"{Value(n, "role")} {Value(n, "name")}")
            .ToList();
        Assert.Contains("region Notifications", exposed);
        Assert.Contains("button Dismiss", exposed);
        Assert.Contains("StaticText The book could not be read.", exposed);
        // The control: what is outside the modal is not exposed.
        Assert.DoesNotContain(exposed, n => n.StartsWith("button") && n.Contains("New project"));
        Log($"Chromium AX tree exposes the region, the message and Dismiss ({exposed.Count} nodes, nothing outside the modal)");

        static string Value(JsonElement node, string key) =>
            node.TryGetProperty(key, out var v) && v.TryGetProperty("value", out var s) ? s.ToString() : "";
    }

    // Needs a real browser: light dismiss and real focus moves.
    [Fact]
    public async Task Clicks_on_a_toast_keep_a_light_dismiss_dialog_open_and_dismissing_hands_focus_on()
    {
        await GotoAppAsync("projects");
        await FailUploadsAsync();
        await Page.EvaluateAsync("() => window.__confirm()");
        var dialog = Page.Locator("dialog[open]");
        var confirmButton = dialog.Locator(".r2m-confirm-dialog__confirm");
        await Expect(confirmButton).ToBeFocusedAsync();

        await Page.EvaluateAsync("() => { window.__uncaughtCreate(); window.__uncaughtCreate(); }");
        await Expect(Toasts).ToHaveCountAsync(2);
        await Toasts.First.Locator(".r2m-toast__text").ClickAsync(new() { Timeout = 3000 });
        await Expect(dialog).ToBeVisibleAsync();

        // Tab reaches the toast's button from the dialog's last control.
        await confirmButton.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        var first = Toasts.Nth(0).Locator(".r2m-toast__dismiss");
        Assert.True(await HasFocusAsync(first), "Tab should reach the first toast's Dismiss");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Toasts).ToHaveCountAsync(1);
        Assert.True(await HasFocusAsync(Toasts.First.Locator(".r2m-toast__dismiss")), "focus should move to the next toast");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Toasts).ToHaveCountAsync(0);
        await Expect(confirmButton).ToBeFocusedAsync();
        await Expect(dialog).ToBeVisibleAsync();
        Log("clicks keep a closedby=any dialog open; focus goes to the next toast, then back");
    }

    // Needs a real browser: real focus and hover events against real time.
    [Fact]
    public async Task A_focused_Show_in_Book_toast_outlives_12_s_and_runs_out_after_focus_leaves()
    {
        await App.SeedProjectAsync("toast-proof", "Toast Proof", "A. Author");
        await GotoAppAsync("projects/toast-proof/pronunciations");
        await Page.Locator("[data-action=add]").First.ClickAsync();
        await Page.Locator("[data-field=written]").FillAsync("stormy");
        await Page.Locator("[data-field=spoken]").FillAsync("STORM-ee");
        await Page.Locator("[data-action=save]").ClickAsync();

        var toast = Page.Locator(".r2m-toast--info");
        var show = toast.GetByRole(AriaRole.Button, new() { Name = "Show in Book" });
        await show.FocusAsync();
        await Page.WaitForTimeoutAsync(13_000);
        await Expect(toast).ToBeVisibleAsync();
        Assert.True(await HasFocusAsync(show));
        var box = (await toast.BoundingBoxAsync())!;
        Log($"focused toast still up after 13 s, {box.Width}x{box.Height}");
        await Page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"r2m-465-{Engine}.png") });

        // Focus moving between its own buttons keeps it; leaving it lets the rest of its time run.
        await Page.Keyboard.PressAsync("Shift+Tab");
        Assert.True(await HasFocusAsync(toast.Locator(".r2m-toast__dismiss")));
        await Page.Locator("[data-action=add]").First.FocusAsync();
        await Expect(toast).ToBeHiddenAsync(new() { Timeout = 14_000 });

        // The pointer holds a 6 s error past its time, and leaving lets it go.
        await FailUploadsAsync();
        await Page.EvaluateAsync("() => window.__uncaughtCreate()");
        var error = Page.Locator(".r2m-toast--error");
        await error.HoverAsync();
        await Page.WaitForTimeoutAsync(7_500);
        await Expect(error).ToBeVisibleAsync();
        await Page.Mouse.MoveAsync(0, 0);
        await Expect(error).ToBeHiddenAsync(new() { Timeout = 7_500 });
        Log("pause and resume under focus and pointer ok");
    }
}
