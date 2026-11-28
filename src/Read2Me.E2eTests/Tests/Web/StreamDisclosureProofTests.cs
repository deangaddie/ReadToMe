using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #502: the collapsed "Show AI activity" strip is skipped by Tab and the
/// accessibility tree, reopens at the latest entry, and only slides when motion is allowed.
/// Deleted before merge; the source and both pass outputs go in the PR body.
/// </summary>
public abstract class StreamDisclosureProof(E2eAppFixture app, PlaywrightFixture pw, ReducedMotion motion) : E2eTestBase(app, pw)
{
    private const string Mark = "STREAMMARK";

    /// <summary>The stream's scroll position, and how far it is from its bottom.</summary>
    private const string Scroll = """
        e => JSON.stringify({ top: e.scrollTop, fromBottom: e.scrollHeight - e.clientHeight - e.scrollTop, height: e.clientHeight, content: e.scrollHeight })
        """;

    private sealed record Position(double top, double fromBottom, double height, double content);

    // Needs a real browser: sequential focus, the accessibility tree and scroll under content-visibility.
    [Fact]
    public async Task The_collapsed_strip_is_skipped_and_reopens_at_the_latest_entry()
    {
        var folder = $"proof-strip-{motion}".ToLowerInvariant();
        await App.SeedProjectAsync(folder, $"Proof Strip {motion}", "A. Author");
        await Page.EmulateMediaAsync(new() { ReducedMotion = motion });
        var reasoning = string.Join("\\n", Enumerable.Range(1, 60).Select(i => $"{Mark} line {i}"));
        App.FakeAi.LlmReply = _ => $$"""{ "reasoning": "{{reasoning}}", "characters": [ { "name": "Link", "aliases": [] } ] }""";
        App.FakeAi.HoldLlmAfter(0);

        await GotoAppAsync($"projects/{folder}/cast?discover=1");
        var dialog = Page.GetByRole(AriaRole.Dialog);
        var toggle = dialog.Locator("[data-action='toggle-stream']");
        var body = dialog.Locator(".r2m-stream-disclosure__body");
        var scroller = dialog.Locator(".r2m-stream-llm__scroll");
        var turns = dialog.Locator(".r2m-stream-llm__turn");
        var jump = dialog.Locator(".r2m-stream-llm__jump");

        // A request is streaming, the strip is closed.
        await Expect(turns).ToHaveCountAsync(1, new() { Timeout = 20_000 });
        await Expect(turns.First).ToHaveAttributeAsync("data-state", "open");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        var controls = await toggle.GetAttributeAsync("aria-controls");
        Assert.False(string.IsNullOrEmpty(controls));
        Assert.Equal(controls, await body.GetAttributeAsync("id"));
        Assert.Equal(1, await Page.Locator($"[id='{controls}']").CountAsync());

        await toggle.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeFocusedAsync();

        var closed = await dialog.AriaSnapshotAsync();
        Assert.DoesNotContain("Prompt", closed);
        Assert.DoesNotContain("Streaming", closed);

        // The turn completes while hidden.
        App.FakeAi.ReleaseLlm();
        await Expect(turns.First).ToHaveAttributeAsync("data-state", "completed");
        await Expect(dialog.Locator("[data-phase='review']")).ToBeAttachedAsync();
        await toggle.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeFocusedAsync();
        closed = await dialog.AriaSnapshotAsync();
        Assert.DoesNotContain(Mark, closed);
        Assert.DoesNotContain("Completed", closed);

        // First open: the latest entry is at the bottom, and the content is exposed.
        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await AtBottomAsync(scroller, "first open", jump);
        var open = await dialog.AriaSnapshotAsync();
        Assert.Contains(Mark, open);
        Assert.Contains("Completed", open);
        await toggle.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        Assert.True(await Page.EvaluateAsync<bool>("() => !!document.activeElement.closest('.r2m-stream-disclosure__body')"),
            "Tab from the open toggle should walk into the stream");
        // Focus may have scrolled the stream up, which is the user pausing: go back to the latest.
        await scroller.EvaluateAsync("e => { e.scrollTop = e.scrollHeight; }");
        await Expect(jump).ToHaveCountAsync(0);

        // Close, stream another request while hidden, reopen: the new latest entry is at the bottom.
        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await SettledAsync(body);
        // Count the reads of the stream's size, which would lay the skipped content out.
        await scroller.EvaluateAsync("""
            e => {
                const read = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollHeight').get;
                window.reads = 0;
                Object.defineProperty(e, 'scrollHeight', { configurable: true, get() { window.reads++; return read.call(this); } });
            }
            """);
        App.FakeAi.HoldLlmAfter(0);
        await dialog.Locator("[data-action='rerun']").ClickAsync();
        await Expect(turns).ToHaveCountAsync(2);
        App.FakeAi.ReleaseLlm();
        await Expect(turns.Nth(1)).ToHaveAttributeAsync("data-state", "completed");
        var reads = await scroller.EvaluateAsync<int>("e => { const n = window.reads; delete e.scrollHeight; return n; }");
        TestContext.Current.TestOutputHelper!.WriteLine($"size reads while closed: {reads}");
        Assert.Equal(0, reads);
        await toggle.ClickAsync();
        await AtBottomAsync(scroller, "reopen after streaming while hidden", jump);
        await Expect(turns.Nth(1).Locator(".r2m-stream-llm__response")).ToBeInViewportAsync();

        // Streaming continues to autoscroll while open.
        await dialog.Locator("[data-action='rerun']").ClickAsync();
        await Expect(turns).ToHaveCountAsync(3);
        await Expect(turns.Nth(2)).ToHaveAttributeAsync("data-state", "completed");
        await AtBottomAsync(scroller, "autoscroll while open", jump);

        // A user who paused it finds it where they left it.
        await scroller.EvaluateAsync("e => { e.scrollTop = 40; }");
        await Expect(jump).ToBeVisibleAsync();
        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(body).ToHaveJSPropertyAsync("offsetHeight", 0);
        await dialog.Locator("[data-action='rerun']").ClickAsync();
        await Expect(turns).ToHaveCountAsync(4);
        await Expect(turns.Nth(3)).ToHaveAttributeAsync("data-state", "completed");
        // Paused, so "Jump to latest" is rendered inside the closed body: Tab must pass it too.
        await Expect(jump).ToHaveCountAsync(1);
        await toggle.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeFocusedAsync();
        Assert.DoesNotContain("Jump to latest", await dialog.AriaSnapshotAsync());
        await toggle.ClickAsync();
        await SettledAsync(body);
        var paused = await PositionAsync(scroller);
        TestContext.Current.TestOutputHelper!.WriteLine($"paused reopen: {paused}");
        Assert.InRange(paused.top, 39, 41);
        await Expect(jump).ToBeVisibleAsync();
        await jump.ClickAsync();
        await AtBottomAsync(scroller, "jump after paused reopen", jump);

        // Content arriving while the strip is still sliding shut is not the user pausing.
        await toggle.EvaluateAsync("""
            async t => {
                const stream = t.nextElementSibling.querySelector('r2m-stream-llm');
                t.click();
                await new Promise(r => setTimeout(r, 40));
                stream.events = [...stream.events, ...Array.from({ length: 8 }, () => ({ kind: 'runStarted' }))];
            }
            """);
        await SettledAsync(body);
        await toggle.ClickAsync();
        await AtBottomAsync(scroller, "reopen after content arrived mid-close", jump);
    }

    // Needs a real browser: computed transitions under an emulated motion preference.
    [Fact]
    public async Task The_strip_slides_only_when_motion_is_allowed()
    {
        Assert.SkipWhen(motion == ReducedMotion.Reduce, "Emulates both preferences itself; runs once.");
        await App.SeedProjectAsync("proof-motion", "Proof Motion", "A. Author");
        App.FakeAi.LlmReply = _ => """{ "reasoning": "r", "characters": [] }""";

        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await GotoAppAsync("projects/proof-motion/cast?discover=1");
        var dialog = Page.GetByRole(AriaRole.Dialog);
        var toggle = dialog.Locator("[data-action='toggle-stream']");
        var body = dialog.Locator(".r2m-stream-disclosure__body");
        await Expect(body).ToBeAttachedAsync(new() { Timeout = 20_000 });

        foreach (var motion in new[] { ReducedMotion.Reduce, ReducedMotion.NoPreference })
        {
            await Page.EmulateMediaAsync(new() { ReducedMotion = motion });
            var closed = await TransitionAsync(body);
            // Height right after the click, before any frame could have slid it.
            var immediate = await toggle.EvaluateAsync<double[]>("""
                async t => {
                    t.click();
                    await Promise.resolve(); await Promise.resolve();
                    const b = t.nextElementSibling;
                    return [b.offsetHeight, innerHeight * 0.32];
                }
                """);
            var open = await TransitionAsync(body);
            await SettledAsync(body);
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"{motion}: closed '{closed}', open '{open}', height at click {immediate[0]} of {immediate[1]}");
            if (motion == ReducedMotion.Reduce)
            {
                Assert.Equal("all 0s", closed);
                Assert.Equal("all 0s", open);
                Assert.InRange(immediate[0], immediate[1] - 1, immediate[1] + 1);
            }
            else
            {
                Assert.Contains("height", closed);
                Assert.Contains("height", open);
                Assert.True(immediate[0] < immediate[1] - 1, $"expected a slide, height at click was {immediate[0]}");
            }

            // Closing: under no-preference the content stays painted while it slides shut.
            var midClose = await toggle.EvaluateAsync<string>("""
                async t => {
                    t.click();
                    await new Promise(r => setTimeout(r, 60));
                    const b = t.nextElementSibling;
                    return `${getComputedStyle(b).contentVisibility} at height ${b.offsetHeight}`;
                }
                """);
            TestContext.Current.TestOutputHelper!.WriteLine($"{motion}: 60 ms into closing, content-visibility {midClose}");
            Assert.StartsWith(motion == ReducedMotion.Reduce ? "hidden" : "visible", midClose);
            await SettledAsync(body);
            await Expect(body).ToHaveJSPropertyAsync("offsetHeight", 0);
            await Expect(body).ToHaveCSSAsync("content-visibility", "hidden");
        }
    }

    private static Task<string> TransitionAsync(ILocator body) =>
        body.EvaluateAsync<string>("e => { const s = getComputedStyle(e); return `${s.transitionProperty} ${s.transitionDuration}`; }");

    private static Task SettledAsync(ILocator body) =>
        body.EvaluateAsync("e => Promise.all(e.getAnimations().map(a => a.finished)).then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))");

    private static async Task<Position> PositionAsync(ILocator scroller) =>
        JsonSerializer.Deserialize<Position>(await scroller.EvaluateAsync<string>(Scroll))!;

    private async Task AtBottomAsync(ILocator scroller, string when, ILocator jump)
    {
        await SettledAsync(scroller.Page.Locator(".r2m-stream-disclosure__body"));
        var position = await PositionAsync(scroller);
        TestContext.Current.TestOutputHelper!.WriteLine($"{when}: {position}, jump buttons {await jump.CountAsync()}");
        Assert.True(position.content > position.height, $"{when}: the stream should overflow, {position}");
        Assert.True(position.height > 100, $"{when}: the stream should have height, {position}");
        Assert.InRange(position.fromBottom, -1, 8);
        await Expect(jump).ToHaveCountAsync(0);
    }
}

// A host each: the hub replays the last LLM turn to the next page that joins the stream.
public class StreamDisclosureProofTests(E2eAppFixture app, PlaywrightFixture pw)
    : StreamDisclosureProof(app, pw, ReducedMotion.NoPreference);

public class StreamDisclosureReducedMotionProofTests(E2eAppFixture app, PlaywrightFixture pw)
    : StreamDisclosureProof(app, pw, ReducedMotion.Reduce);
