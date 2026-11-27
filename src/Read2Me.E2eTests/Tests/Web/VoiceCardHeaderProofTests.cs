using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #476 part 2: the voice card's header controls sit outside the button that
/// opens it, that button is named by the voice alone, the header looks and behaves as before, and
/// open/close, set default and rename work by mouse and keyboard. Writes what it read to
/// <c>R2M_PROOF_OUT</c>. In Firefox the platform tree is read through Windows UI Automation
/// (<c>R2M_UIA_SCRIPT</c>, needs <c>E2E_HEADED=1</c>). Deleted before merge.
/// </summary>
public class VoiceCardHeaderProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly bool _firefox = pw.IsFirefox;
    private readonly StringBuilder _log = new();

    private static string OutDir => Environment.GetEnvironmentVariable("R2M_PROOF_OUT") ?? Path.GetTempPath();

    private void Section(string title, string body) =>
        _log.AppendLine($"=== {title} ===").AppendLine(body.TrimEnd()).AppendLine();

    private async Task<(ILocator Seeded, ILocator Added, string AddedId)> OpenCastAsync()
    {
        var folder = $"voice-header-{Guid.NewGuid():N}";
        var book = await App.SeedProjectAsync(folder, "Pride and Prejudice", "Jane Austen", characterName: "Mary Bennet");
        var mary = book.CharacterId("Mary Bennet");
        var seeded = await App.SeedEditableVoiceAsync(folder, mary, "Mary Bennet");
        using var http = new HttpClient { BaseAddress = new Uri(App.BaseUrl) };
        var created = await http.PostAsJsonAsync($"/api/projects/{folder}/commands",
            new { type = "CreateVoice", characterId = mary, name = "At the pianoforte", isGenerated = false });
        created.EnsureSuccessStatusCode();
        var added = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("newEntityId").GetString()!;

        await GotoAppAsync($"projects/{folder}/cast/{mary}");
        ILocator Card(string id) => Page.Locator($"r2m-voice-card[data-voice-id='{id}']");
        await Expect(Card(added).Locator(".voice-card__header")).ToBeVisibleAsync();
        return (Card(seeded.ToString()), Card(added), added);
    }

    /// <summary>Where the header's parts sit, relative to the card: the same numbers before and after.</summary>
    private static Task<string> LayoutAsync(ILocator card) => card.EvaluateAsync<string>(
        """
        card => {
          const at = card.getBoundingClientRect();
          return ['.voice-card__header', '.voice-card__star', '.r2m-inline-edit__display',
                  '.voice-card__meta .r2m-status-chip', '.voice-card__delete']
            .map(s => {
              const r = card.querySelector(s).getBoundingClientRect();
              return `${s}: x ${Math.round(r.x - at.x)} y ${Math.round(r.y - at.y)} ${Math.round(r.width)}x${Math.round(r.height)}`;
            }).join('\n');
        }
        """);

    // Needs a real browser: the toggle lies under the controls by paint order and hit testing.
    [Fact]
    public async Task The_button_is_named_by_the_voice_and_the_controls_stand_outside_it()
    {
        var (_, card, id) = await OpenCastAsync();
        var browser = _firefox ? "firefox" : "chromium";
        var header = card.Locator(".voice-card__header");
        Section("Header layout, closed (relative to the card)", await LayoutAsync(card));
        await card.ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"{Label}-{browser}-card.png") });
        Section("ariaSnapshot: voice card header", await header.AriaSnapshotAsync());
        Section("Platform tree: voice card header", await TreeAsync(id, header));
        await Flush(browser);

        var toggle = card.Locator(".voice-card__toggle");
        await Expect(toggle).ToHaveCountAsync(1);
        await Expect(toggle).ToHaveAccessibleNameAsync("At the pianoforte");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Level = 4, Name = "At the pianoforte", Exact = true }))
            .ToHaveCountAsync(1);
        await Expect(card.Locator("summary button, summary a, summary r2m-inline-edit, button button, button a"))
            .ToHaveCountAsync(0);

        await toggle.FocusAsync();
        var stops = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await Page.Keyboard.PressAsync("Tab");
            stops.Add(await Page.EvaluateAsync<string>("() => document.activeElement.getAttribute('aria-label')"));
        }
        Section("Tab stops after the toggle", string.Join('\n', stops));
        Assert.Equal(["Set as default voice", "Edit Voice name: At the pianoforte", "Delete voice"], stops);

        // The focus ring takes the whole header, as the summary's did.
        for (var i = 0; i < 3; i++) await Page.Keyboard.PressAsync("Shift+Tab");
        await Expect(toggle).ToBeFocusedAsync();
        await Expect(toggle).ToHaveCSSAsync("outline-style", "solid");
        await Expect(toggle).ToHaveCSSAsync("outline-width", "2px");
        var headerBox = (await header.BoundingBoxAsync())!;
        var toggleBox = (await toggle.BoundingBoxAsync())!;
        Assert.Equal((headerBox.X, headerBox.Y, headerBox.Width, headerBox.Height),
            (toggleBox.X, toggleBox.Y, toggleBox.Width, toggleBox.Height));
        await card.ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"{Label}-{browser}-card-focus.png") });

        // The chevron turns with the state.
        var chevron = toggle.Locator(".r2m-icon");
        await Expect(chevron).ToHaveCSSAsync("rotate", "none");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(chevron).ToHaveCSSAsync("rotate", "180deg");
        await Page.Keyboard.PressAsync("Space");
        await Expect(chevron).ToHaveCSSAsync("rotate", "none");

        // A fragment inside the closed body reveals it, as find-in-page does.
        var advanced = card.Locator("[data-section='advanced'] > summary");
        await Expect(advanced).ToBeHiddenAsync();
        await advanced.EvaluateAsync("el => { el.id = 'proof-hidden'; location.hash = 'proof-hidden'; }");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(advanced).ToBeVisibleAsync();
        Section("Reveal", "a fragment inside the closed body opened the card (beforematch)");
        await Flush(browser);
    }

    // Needs a real browser: which element a click on the covered header lands on is the engine's hit test.
    [Fact]
    public async Task Open_close_default_and_rename_work_by_mouse_and_keyboard()
    {
        var (first, second, _) = await OpenCastAsync();
        var toggle = second.Locator(".voice-card__toggle");
        var header = second.Locator(".voice-card__header");
        var advanced = second.Locator("[data-section='advanced'] > summary");
        async Task IsOpenAsync(bool open)
        {
            await Expect(toggle).ToHaveAttributeAsync("aria-expanded", open ? "true" : "false");
            if (open) await Expect(advanced).ToBeVisibleAsync();
            else await Expect(advanced).ToBeHiddenAsync();
        }

        // Open and close by mouse: the header's padding, its bare middle, the chevron.
        var box = (await header.BoundingBoxAsync())!;
        await IsOpenAsync(false);
        foreach (var x in new[] { 4f, box.Width / 2, box.Width - 22 })
        {
            await Page.Mouse.ClickAsync(box.X + x, box.Y + box.Height / 2);
            await IsOpenAsync(true);
            await Page.Mouse.ClickAsync(box.X + x, box.Y + box.Height / 2);
            await IsOpenAsync(false);
        }
        // And by keyboard.
        foreach (var key in new[] { "Enter", "Space" })
        {
            await toggle.FocusAsync();
            await Page.Keyboard.PressAsync(key);
            await IsOpenAsync(true);
            await Page.Keyboard.PressAsync(key);
            await IsOpenAsync(false);
        }
        // A chip is on top of the toggle, so its tooltip still gets the pointer; it opens nothing.
        await second.Locator(".voice-card__meta .r2m-status-chip").First.ClickAsync();
        await IsOpenAsync(false);
        // Nor does a near miss between the right-hand controls.
        var chip = (await second.Locator(".voice-card__meta .r2m-status-chip").First.BoundingBoxAsync())!;
        var trash = (await second.Locator("[data-action='delete-voice']").BoundingBoxAsync())!;
        Assert.True(trash.X - (chip.X + chip.Width) >= 2, "no gap between the chip and Delete");
        await Page.Mouse.ClickAsync((chip.X + chip.Width + trash.X) / 2, trash.Y + trash.Height / 2);
        await IsOpenAsync(false);

        // Default voice: Enter on the second card's star, the mouse on the first's, Space on the second's.
        async Task IsDefaultAsync(ILocator now, ILocator other)
        {
            await Expect(now.Locator(".voice-card__star--on")).ToBeVisibleAsync();
            await Expect(other.Locator("[data-action='set-default']")).ToBeVisibleAsync();
            await Expect(now).Not.ToHaveAttributeAsync("aria-busy", "true");
            await Expect(other).Not.ToHaveAttributeAsync("aria-busy", "true");
        }
        await second.Locator("[data-action='set-default']").FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await IsDefaultAsync(second, first);
        // The lit star keeps its tooltip: it is the element under the pointer.
        await second.Locator(".voice-card__star--on").HoverAsync();
        await Expect(Page.Locator(".r2m-tooltip").Filter(new() { HasText = "Default voice" })).ToBeVisibleAsync();
        // And a click on it opens the card, as it did inside the summary.
        await second.Locator(".voice-card__star--on").ClickAsync();
        await IsOpenAsync(true);
        await second.Locator(".voice-card__star--on").ClickAsync();
        await IsOpenAsync(false);
        await first.Locator("[data-action='set-default']").ClickAsync();
        await IsDefaultAsync(first, second);
        await second.Locator("[data-action='set-default']").FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await IsDefaultAsync(second, first);
        await IsOpenAsync(false);

        // Rename: keyboard (with spaces typed key by key), then mouse.
        var display = second.Locator(".r2m-inline-edit__display");
        var input = second.Locator(".r2m-inline-edit__input");
        await display.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(input).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.TypeAsync("At the ball");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(display).ToHaveAccessibleNameAsync("Edit Voice name: At the ball");
        await Expect(toggle).ToHaveAccessibleNameAsync("At the ball");
        await Expect(second).Not.ToHaveAttributeAsync("aria-busy", "true");
        await display.FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await Expect(input).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await display.ClickAsync();
        await input.ClickAsync();
        await input.FillAsync("At Netherfield");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(toggle).ToHaveAccessibleNameAsync("At Netherfield");
        await IsOpenAsync(false);

        // Delete asks first, and asking does not open the card.
        await second.Locator("[data-action='delete-voice']").ClickAsync();
        await Expect(Page.Locator("r2m-confirm-dialog")).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Escape");
        await IsOpenAsync(false);
    }

    private static string Label => Environment.GetEnvironmentVariable("R2M_PROOF_LABEL") ?? "proof";

    private Task Flush(string browser) =>
        File.WriteAllTextAsync(Path.Combine(OutDir, $"header-{browser}.txt"), _log.ToString());

    private Task<string> TreeAsync(string id, ILocator header) =>
        _firefox ? UiaTreeAsync(id, header) : CdpTreeAsync(header);

    private async Task<string> CdpTreeAsync(ILocator element)
    {
        await element.EvaluateAsync("el => { window.__proof = el; }");
        var cdp = await Page.Context.NewCDPSessionAsync(Page);
        var evaluated = await cdp.SendAsync("Runtime.evaluate", new() { ["expression"] = "window.__proof" });
        var objectId = evaluated!.Value.GetProperty("result").GetProperty("objectId").GetString()!;
        var described = await cdp.SendAsync("DOM.describeNode", new() { ["objectId"] = objectId });
        var backend = described!.Value.GetProperty("node").GetProperty("backendNodeId").GetInt32();
        var tree = await cdp.SendAsync("Accessibility.getFullAXTree");
        var nodes = tree!.Value.GetProperty("nodes").EnumerateArray()
            .DistinctBy(n => n.GetProperty("nodeId").GetString())
            .ToDictionary(n => n.GetProperty("nodeId").GetString()!);
        var root = nodes.Values.First(n => n.TryGetProperty("backendDOMNodeId", out var b) && b.GetInt32() == backend);
        var text = new StringBuilder();
        Walk(root, 0);
        await cdp.DetachAsync();
        return text.ToString();

        void Walk(JsonElement n, int depth)
        {
            static string Value(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.TryGetProperty("value", out var x) ? x.ToString() : "";
            var role = Value(n, "role");
            var shown = !n.GetProperty("ignored").GetBoolean() && role is not ("generic" or "none" or "InlineTextBox");
            if (shown)
            {
                var focusable = n.TryGetProperty("properties", out var ps) && ps.EnumerateArray()
                    .Any(p => p.GetProperty("name").GetString() == "focusable");
                text.Append(' ', depth * 2).AppendLine($"{role} \"{Value(n, "name")}\"{(focusable ? " [focusable]" : "")}");
            }
            if (!n.TryGetProperty("childIds", out var kids)) return;
            foreach (var kid in kids.EnumerateArray())
                if (nodes.TryGetValue(kid.GetString()!, out var child)) Walk(child, depth + (shown ? 1 : 0));
        }
    }

    private async Task<string> UiaTreeAsync(string id, ILocator header)
    {
        var title = $"proof-{id}";
        // The app writes its own title as data lands, so hold ours against it.
        await Page.EvaluateAsync("t => { clearInterval(window.__title); window.__title = setInterval(() => { document.title = t; }, 100); }", title);
        await header.ScrollIntoViewIfNeededAsync();
        var script = Environment.GetEnvironmentVariable("R2M_UIA_SCRIPT");
        if (script is null) return "(R2M_UIA_SCRIPT not set)";
        // Firefox updates the platform tree after the DOM; give it a beat.
        await Page.WaitForTimeoutAsync(1000);
        // The first node naming the voice is its heading button (or the summary); climb to the header.
        var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-File", script, "-Title", title, "-Name", "At the pianoforte", "-Up", "2"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return await output + await error;
    }
}
