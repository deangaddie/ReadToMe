using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #504: each stream log has one header control that pauses and resumes the
/// autoscroll, off-screen entries are skipped, every turn summary stays in the Tab order and the
/// autoscroll still lands at the true bottom. The events go through the host's real broadcasters,
/// relay and hub.
/// </summary>
public class StreamScrollProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private int _sent;

    private string Engine => Page.Context.Browser!.BrowserType.Name;

    private void Log(string line) => TestContext.Current.TestOutputHelper!.WriteLine($"{Engine}: {line}");

    private void Send(string kind, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var n = ++_sent;
            if (kind == "llm")
            {
                var llm = App.Services.GetRequiredService<EventBroadcaster<LlmStreamEvent>>();
                llm.Publish(new RequestStarted($"{n}: Elizabeth leaned back in her chair",
                    string.Join('\n', Enumerable.Repeat("It is a truth universally acknowledged.", 80)), 1, "qwen-28b"));
                llm.Publish(new ContentDelta("{\n  \"items\": [\n    { \"id\": 1, \"speaker\": \"Elizabeth\" },\n    { \"id\": 2, \"speaker\": \"Bingley\" }\n  ]\n}"));
                llm.Publish(new StreamCompleted(900, 42, 1200, 35));
            }
            else
            {
                var audio = App.Services.GetRequiredService<EventBroadcaster<AudioGenEvent>>();
                var id = Guid.NewGuid();
                audio.Publish(new ItemStarted(id, 1, "Elizabeth", $"{n}: It is a truth universally acknowledged."));
                audio.Publish(new AudioGenerated(id, 1));
                audio.Publish(new Normalized(id, 1, true, null));
                audio.Publish(new PostProcessed(id, 1, "silence-trim", true, null));
                audio.Publish(new Transcribed(id, 1, "It is a truth universally acknowledged."));
                audio.Publish(new Verified(id, 1, true, 0.02, null));
            }
        }
    }

    private ILocator Scroller(string kind) => Page.Locator($".r2m-stream-{kind}__scroll");

    private ILocator Entries(string kind) =>
        Page.Locator(kind == "llm" ? ".r2m-stream-llm__turn" : ".r2m-stream-audio__card");

    private ILocator Control(string kind) => Page.Locator($".r2m-stream-{kind}__follow");

    private static async Task<double> Top(ILocator scroller) =>
        await scroller.EvaluateAsync<double>("el => el.scrollTop");

    private static async Task<double> Gap(ILocator scroller) =>
        await scroller.EvaluateAsync<double>("el => el.scrollHeight - el.clientHeight - el.scrollTop");

    /// <summary>The scroll position once two frames have passed and it has stopped moving.</summary>
    private static async Task<double> SettledTop(ILocator scroller) =>
        await scroller.EvaluateAsync<double>(
            """
            async (el) => {
              let last = -1, same = 0;
              while (same < 10) {
                await new Promise((done) => requestAnimationFrame(done));
                same = el.scrollTop === last ? same + 1 : 0;
                last = el.scrollTop;
              }
              return last;
            }
            """);

    private async Task ExpectBottom(string kind, string when)
    {
        await SettledTop(Scroller(kind));
        var gap = await Gap(Scroller(kind));
        Log($"{kind} {when}: scrollTop {await Top(Scroller(kind))}, distance from bottom {gap}");
        Assert.InRange(gap, -1, 1);
    }

    private async Task OpenStream(string kind, string tab)
    {
        await GotoAppAsync("projects");
        await Page.Locator(".activity-bar__toggle").ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = tab }).ClickAsync();
        await Expect(Scroller(kind)).ToBeVisibleAsync();
        // The stream group is joined by an unawaited hub call: send until the first entry shows.
        for (var i = 0; i < 100 && await Entries(kind).CountAsync() == 0; i++)
        {
            Send(kind, 1);
            await Page.WaitForTimeoutAsync(100);
        }
    }

    private async Task More(string kind, int count)
    {
        Send(kind, count);
        var last = $"{_sent}: ";
        await Expect(Entries(kind).Last).ToContainTextAsync(last);
    }

    // Needs a real browser: real scrolling, keyboard and wheel input, and layout.
    [Theory]
    [InlineData("llm", "LLM")]
    [InlineData("audio", "Audio")]
    public async Task The_control_pauses_and_resumes_the_autoscroll(string kind, string tab)
    {
        await OpenStream(kind, tab);
        await More(kind, 12);
        var control = Control(kind);
        await Expect(control).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("Pause scrolling"));
        Assert.Null(await control.GetAttributeAsync("aria-pressed"));
        await ExpectBottom(kind, "following");

        // Pause by keyboard: new entries arrive, the view holds.
        await control.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(control).ToContainTextAsync("Jump to latest");
        var held = await SettledTop(Scroller(kind));
        await More(kind, 5);
        Assert.Equal(held, await SettledTop(Scroller(kind)));
        Log($"{kind} paused by Enter: scrollTop {held} before and after 5 new entries; distance from bottom {await Gap(Scroller(kind))}");
        await Expect(control).ToBeFocusedAsync();

        // Resume by keyboard: back at the bottom, and following again.
        await Page.Keyboard.PressAsync("Space");
        await Expect(control).ToContainTextAsync("Pause scrolling");
        await ExpectBottom(kind, "resumed by Space");
        await More(kind, 3);
        await ExpectBottom(kind, "following new entries");

        // Wheel up while entries keep arriving.
        var run = new CancellationTokenSource();
        var streaming = Task.Run(async () =>
        {
            while (!run.IsCancellationRequested)
            {
                Send(kind, 1);
                await Task.Delay(100);
            }
        });
        await Scroller(kind).HoverAsync();
        await Page.Mouse.WheelAsync(0, -700);
        await Expect(control).ToContainTextAsync("Jump to latest");
        var up = await SettledTop(Scroller(kind));
        var count = _sent;
        await Page.WaitForTimeoutAsync(700);
        Assert.True(_sent > count);
        var later = await SettledTop(Scroller(kind));
        Log($"{kind} wheeled up mid-run: scrollTop {up}, {later} after {_sent - count} more entries; distance from bottom {await Gap(Scroller(kind))}");
        Assert.True(await Gap(Scroller(kind)) > 100);
        await control.ClickAsync();
        await Expect(control).ToContainTextAsync("Pause scrolling");
        await Page.WaitForTimeoutAsync(700);
        await Expect(control).ToContainTextAsync("Pause scrolling");
        await run.CancelAsync();
        await streaming;
        await More(kind, 1);
        await ExpectBottom(kind, "jumped to latest mid-run");

        // Wheel back to the bottom resumes.
        await Scroller(kind).HoverAsync();
        await Page.Mouse.WheelAsync(0, -700);
        await Expect(control).ToContainTextAsync("Jump to latest");
        await SettledTop(Scroller(kind));
        for (var i = 0; i < 20 && await Gap(Scroller(kind)) > 1; i++)
        {
            await Page.Mouse.WheelAsync(0, 800);
            await SettledTop(Scroller(kind));
        }
        await Expect(control).ToContainTextAsync("Pause scrolling");
        await ExpectBottom(kind, "wheeled back down");

        // Keys: Home pauses and reaches the top; the view holds while entries arrive. PageUp too. End resumes.
        // Focus as a keyboard user reaches it, with the shipped markup: the first Tab after the control
        // lands on the scroller itself or the first entry's summary.
        await control.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        var focused = await Page.EvaluateAsync<string>("() => document.activeElement.tagName + '.' + document.activeElement.className");
        Log($"{kind} Tab from the control focuses {focused}");
        if (focused.Contains("__head"))
        {
            // The first summary: Tab scrolled up to it, which pauses. Go back to the bottom and
            // press PageUp from the last turn's summary instead.
            await Expect(control).ToContainTextAsync("Jump to latest");
            await control.ClickAsync();
            await ExpectBottom(kind, "resumed before the key");
            await Page.Locator(".r2m-stream-llm__head").Last.FocusAsync();
            await ExpectBottom(kind, "last summary focused");
        }
        await Page.Keyboard.PressAsync("PageUp");
        await Expect(control).ToContainTextAsync("Jump to latest");
        Log($"{kind} PageUp: scrollTop {await SettledTop(Scroller(kind))}");
        await Page.Keyboard.PressAsync("Home");
        Assert.Equal(0, await SettledTop(Scroller(kind)));
        await Expect(control).ToContainTextAsync("Jump to latest");
        Log($"{kind} Home: scrollTop {await Top(Scroller(kind))}");
        await Page.Keyboard.PressAsync("End");
        await Expect(control).ToContainTextAsync("Pause scrolling");
        await ExpectBottom(kind, "End");

        var heights = await Entries(kind).EvaluateAllAsync<double[]>(
            "els => els.slice(-4).map(e => e.getBoundingClientRect().height)");
        Log($"{kind} entry heights: {string.Join(", ", heights)}");
        await Page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"r2m-504-{Engine}-{kind}.png") });
    }

    // Needs a real browser: content-visibility, sequential focus navigation and layout.
    [Theory]
    [InlineData("llm", "LLM")]
    [InlineData("audio", "Audio")]
    public async Task Fifty_entries_skip_off_screen_and_land_at_the_true_bottom(string kind, string tab)
    {
        await OpenStream(kind, tab);
        await More(kind, 55);
        await Expect(Entries(kind)).ToHaveCountAsync(50);
        await ExpectBottom(kind, "50 entries");

        const string Rendered = "els => els.map(e => e.firstElementChild.checkVisibility({ contentVisibilityAuto: true }))";
        var rendered = await Entries(kind).EvaluateAllAsync<bool[]>(Rendered);
        Log($"{kind}: {rendered.Count(r => r)} of {rendered.Length} entries rendered at the bottom");
        Assert.False(rendered[0]);
        Assert.True(rendered[^1]);

        // A list that first appears with 50 entries (a dialog's disclosure opening) lands at the
        // bottom too, although every height was an estimate when it scrolled.
        var gap = await Page.EvaluateAsync<double>(
            """
            async (kind) => {
              const source = document.querySelector(`r2m-stream-${kind}`);
              const box = document.createElement('div');
              box.style.cssText = 'position:fixed;inset:100px auto auto 100px;width:420px;height:500px';
              const el = document.createElement(`r2m-stream-${kind}`);
              el.events = source.events;
              box.append(el);
              document.body.append(box);
              await new Promise((done) => setTimeout(done, 1000));
              const s = el.querySelector(`.r2m-stream-${kind}__scroll`);
              const out = s.scrollHeight - s.clientHeight - s.scrollTop;
              const label = el.querySelector('button').textContent.trim();
              box.remove();
              return label.includes('Pause scrolling') ? out : -999;
            }
            """, kind);
        Log($"{kind} fresh element with 50 entries: distance from bottom {gap}");
        Assert.InRange(gap, -1, 1);

        // The AI dialogs' disclosure, under its real CSS: the list is mounted closed, events arrive,
        // and opening it (a height transition) lands at the latest entry, still following.
        foreach (var round in new[] { 1, 2, 3 })
        {
            var opened = await Page.EvaluateAsync<string>(
                """
                async ([kind, round]) => {
                  const source = document.querySelector('r2m-stream-llm, r2m-stream-audio');
                  let body = document.querySelector('#proof-disclosure');
                  if (!body) {
                    const host = document.createElement('div');
                    host.className = 'r2m-stream-disclosure';
                    host.style.cssText = 'position:fixed;inset:100px auto auto 100px;width:600px;background:white';
                    body = document.createElement('div');
                    body.id = 'proof-disclosure';
                    body.className = 'r2m-stream-disclosure__body';
                    const el = document.createElement(`r2m-stream-${kind}`);
                    if (kind === 'llm') el.shown = false;
                    body.append(el);
                    host.append(body);
                    document.body.append(host);
                  }
                  const el = body.firstElementChild;
                  el.events = source.events.slice(0, round * 40);
                  await new Promise((done) => setTimeout(done, 200));
                  body.classList.add('r2m-stream-disclosure__body--open');
                  if (kind === 'llm') el.shown = true;
                  await new Promise((done) => setTimeout(done, 800));
                  const s = el.querySelector(`.r2m-stream-${kind}__scroll`);
                  const label = el.querySelector('button').textContent.includes('Pause scrolling') ? 'Pause scrolling' : 'Jump to latest';
                  const result = `${label}, distance from bottom ${s.scrollHeight - s.clientHeight - s.scrollTop}`;
                  body.classList.remove('r2m-stream-disclosure__body--open');
                  if (kind === 'llm') el.shown = false;
                  await new Promise((done) => setTimeout(done, 400));
                  return result;
                }
                """, new object[] { kind, round });
            Log($"{kind} disclosure opening {round}: {opened}");
            if (kind == "llm") Assert.Equal("Pause scrolling, distance from bottom 0", opened);
        }

        await Page.EvaluateAsync("() => document.querySelector('#proof-disclosure').parentElement.remove()");

        if (kind != "llm") return;

        // Tab from the control reaches every turn's summary in order, off-screen ones included.
        await Control(kind).FocusAsync();
        var order = new List<int>();
        for (var i = 0; i < 400 && order.Count < 50; i++)
        {
            await Page.Keyboard.PressAsync("Tab");
            var at = await Page.EvaluateAsync<int>(
                """
                () => {
                  const el = document.activeElement;
                  if (!el?.matches('.r2m-stream-llm__head')) return -1;
                  const s = el.closest('.r2m-stream-llm__scroll').getBoundingClientRect();
                  const r = el.getBoundingClientRect();
                  if (r.bottom <= s.top || r.top >= s.bottom) return -2;
                  return [...document.querySelectorAll('.r2m-stream-llm__head')].indexOf(el);
                }
                """);
            Assert.NotEqual(-2, at);
            if (at < 0) continue;
            order.Add(at);
            if (at == 2) await Page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"r2m-504-{Engine}-focus.png") });
        }
        Log($"Tab focused turn summaries: {string.Join(' ', order)}");
        Assert.Equal(Enumerable.Range(0, 50), order);
    }
}
