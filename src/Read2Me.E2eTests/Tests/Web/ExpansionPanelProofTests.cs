using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #476: no Expansion panel summary reads its chevron's ligature, the chevron
/// still draws, and the voice card's header controls are separate, operable controls in each engine.
/// Writes what it read to <c>R2M_PROOF_OUT</c>. In Firefox the platform tree is read through
/// Windows UI Automation (<c>R2M_UIA_SCRIPT</c>, needs <c>E2E_HEADED=1</c>). Deleted before merge.
/// </summary>
public class ExpansionPanelProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly bool _firefox = pw.IsFirefox;
    private readonly StringBuilder _log = new();

    private void Section(string title, string body) =>
        _log.AppendLine($"=== {title} ===").AppendLine(body.TrimEnd()).AppendLine();

    private async Task<(ILocator Seeded, ILocator Added, string AddedId)> OpenCastAsync()
    {
        var folder = $"expansion-{Guid.NewGuid():N}";
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
        await Expect(Card(added).Locator("summary.voice-card__header")).ToBeVisibleAsync();
        return (Card(seeded.ToString()), Card(added), added);
    }

    /// <summary>A click on the header's own padding: the summary, none of its controls.</summary>
    private static Task ToggleByMouseAsync(ILocator card) =>
        card.Locator("summary.voice-card__header").ClickAsync(new() { Position = new() { X = 4, Y = 4 } });

    // Needs a real browser: generated content in the accessible name is the engine's.
    [Fact]
    public async Task No_summary_reads_the_chevron_and_the_header_controls_are_separate()
    {
        var (_, card, id) = await OpenCastAsync();
        var header = card.Locator("summary.voice-card__header");
        var advanced = card.Locator("[data-section='advanced'] > summary");
        var transcript = card.Locator("[data-section='transcript'] > summary");

        var headerSnapshot = await header.AriaSnapshotAsync();
        Section("ariaSnapshot: voice card header", headerSnapshot);
        Section("Platform tree: voice card header", await TreeAsync(id, header, "Set as default voice"));

        await ToggleByMouseAsync(card);
        await Expect(advanced).ToBeVisibleAsync();
        var advancedSnapshot = await advanced.AriaSnapshotAsync();
        var transcriptSnapshot = await transcript.AriaSnapshotAsync();
        Section("ariaSnapshot: Expansion panel summary (Advanced settings)", advancedSnapshot);
        Section("ariaSnapshot: Expansion panel summary (Transcript)", transcriptSnapshot);
        Section("Platform tree: Advanced settings summary", await TreeAsync(id, advanced, "Advanced settings"));
        await ToggleByMouseAsync(card);

        // The header's controls, in tab order after the summary.
        await header.FocusAsync();
        var stops = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await Page.Keyboard.PressAsync("Tab");
            stops.Add(await Page.EvaluateAsync<string>(
                "() => document.activeElement.getAttribute('aria-label')"));
        }
        Section("Tab stops after the header summary", string.Join('\n', stops));

        // The rename input, while it stands in the summary.
        await card.Locator(".r2m-inline-edit__display").ClickAsync();
        await Expect(card.Locator(".r2m-inline-edit__input")).ToBeFocusedAsync();
        Section("ariaSnapshot: voice card header while renaming", await header.AriaSnapshotAsync());
        Section("Platform tree: voice card header while renaming", await TreeAsync(id, header, "Set as default voice"));
        await Page.Keyboard.PressAsync("Escape");

        var chevron = await advanced.EvaluateAsync<string>(
            "el => { const s = getComputedStyle(el, '::after'); return `content ${s.content} | width ${s.width} | ${s.fontFamily}`; }");
        Section("Chevron (computed ::after)", chevron);

        var outDir = Environment.GetEnvironmentVariable("R2M_PROOF_OUT") ?? Path.GetTempPath();
        await File.WriteAllTextAsync(Path.Combine(outDir, $"proof-{(_firefox ? "firefox" : "chromium")}.txt"), _log.ToString());

        Assert.Equal(["Set as default voice", "Edit Voice name: At the pianoforte", "Delete voice"], stops);
        foreach (var snapshot in new[] { headerSnapshot, advancedSnapshot, transcriptSnapshot })
            Assert.DoesNotContain("expand_more", snapshot);
        Assert.Contains("button \"Set as default voice\"", headerSnapshot);
        Assert.Contains("button \"Edit Voice name: At the pianoforte\"", headerSnapshot);
        Assert.DoesNotContain("expand_more", _log.ToString().Split("=== Chevron")[0]);
        // The ligature still draws as one glyph, 20 px wide, and turns when the panel opens.
        Assert.Contains("width 20px", chevron);
        await ToggleByMouseAsync(card);
        await Expect(advanced).ToBeVisibleAsync();
        Assert.Equal("180deg", await header.EvaluateAsync<string>("el => new Promise(r => setTimeout(() => r(getComputedStyle(el, '::after').rotate), 400))"));
    }

    // Needs a real browser: Space and Enter on controls inside a <summary> are the engine's to route.
    [Fact]
    public async Task Open_close_default_and_rename_work_by_mouse_and_keyboard()
    {
        var (first, second, _) = await OpenCastAsync();
        var panel = second.Locator("details.voice-card__panel");
        var header = second.Locator("summary.voice-card__header");
        var open = new Regex(".*");

        // Open and close: mouse, then Enter and Space on the summary.
        await ToggleByMouseAsync(second);
        await Expect(panel).ToHaveAttributeAsync("open", open);
        await ToggleByMouseAsync(second);
        await Expect(panel).Not.ToHaveAttributeAsync("open", open);
        foreach (var key in new[] { "Enter", "Space" })
        {
            await header.FocusAsync();
            await Page.Keyboard.PressAsync(key);
            await Expect(panel).ToHaveAttributeAsync("open", open);
            await Page.Keyboard.PressAsync(key);
            await Expect(panel).Not.ToHaveAttributeAsync("open", open);
        }

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
        await first.Locator("[data-action='set-default']").ClickAsync();
        await IsDefaultAsync(first, second);
        await second.Locator("[data-action='set-default']").FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await IsDefaultAsync(second, first);
        await Expect(panel).Not.ToHaveAttributeAsync("open", open);

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
        await Expect(second).Not.ToHaveAttributeAsync("aria-busy", "true");
        await display.FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await Expect(input).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await display.ClickAsync();
        await input.ClickAsync();
        await input.FillAsync("At Netherfield");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(display).ToHaveAccessibleNameAsync("Edit Voice name: At Netherfield");
        await Expect(panel).Not.ToHaveAttributeAsync("open", open);
    }

    private Task<string> TreeAsync(string id, ILocator summary, string name) =>
        _firefox ? UiaTreeAsync(id, summary, name) : CdpTreeAsync(summary);

    private async Task<string> CdpTreeAsync(ILocator summary)
    {
        await summary.EvaluateAsync("el => { window.__proof = el; }");
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

    private async Task<string> UiaTreeAsync(string id, ILocator summary, string name)
    {
        var title = $"proof-{id}";
        // The app writes its own title as data lands, so hold ours against it.
        await Page.EvaluateAsync("t => { clearInterval(window.__title); window.__title = setInterval(() => { document.title = t; }, 100); }", title);
        await summary.ScrollIntoViewIfNeededAsync();
        var script = Environment.GetEnvironmentVariable("R2M_UIA_SCRIPT");
        if (script is null) return "(R2M_UIA_SCRIPT not set)";
        // Firefox updates the platform tree after the DOM; give it a beat.
        await Page.WaitForTimeoutAsync(1000);
        var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-File", script, "-Title", title, "-Name", name])
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
