using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #479: the progress fill and the spinner in both engines, and under reduced motion.</summary>
public class ProgressProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly string Browser = Environment.GetEnvironmentVariable("R2M_E2E_BROWSER") ?? "chromium";
    private static readonly string Shots = Environment.GetEnvironmentVariable("R2M_PROOF_DIR") ?? Path.GetTempPath();

    private async Task MountAsync()
    {
        await GotoAppAsync("projects");
        await Page.EvaluateAsync("""
            () => {
              const box = document.createElement('div');
              box.id = 'proof';
              box.style.cssText = 'position:fixed;inset:0 auto auto 0;z-index:99999;width:400px;padding:16px;display:grid;gap:16px;background:var(--r2m-surface)';
              box.innerHTML = '<progress id="ind" max="1" aria-label="Loading"></progress>'
                + '<progress id="det" max="1" value="0.4" aria-label="Forty"></progress>'
                + '<span id="spin" class="r2m-spinner" role="progressbar" aria-label="Busy"></span>';
              document.body.append(box);
            }
            """);
    }

    private async Task<Dictionary<string, string>> FillAsync(string id)
    {
        var d = await FillByGcsAsync(id);
        if (Browser == "firefox") return d;
        // Chromium's getComputedStyle ignores ::-webkit-progress-value; read the fill's UA shadow node over CDP.
        var cdp = await Page.Context.NewCDPSessionAsync(Page);
        await cdp.SendAsync("DOM.enable");
        await cdp.SendAsync("CSS.enable");
        var doc = (await cdp.SendAsync("DOM.getDocument", new() { ["depth"] = -1, ["pierce"] = true }))!.Value;
        var node = FindFill(doc.GetProperty("root"), id, false) ?? throw new InvalidOperationException("no fill node");
        var cs = (await cdp.SendAsync("CSS.getComputedStyleForNode", new() { ["nodeId"] = node }))!.Value;
        var map = cs.GetProperty("computedStyle").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("value").GetString()!);
        d["background"] = map["background-color"];
        d["width"] = map["width"];
        d["animationName"] = map["animation-name"];
        d["animationDuration"] = map["animation-duration"];
        return d;
    }

    private static int? FindFill(System.Text.Json.JsonElement n, string id, bool inside)
    {
        var attrs = n.TryGetProperty("attributes", out var a) ? a.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        var here = inside || (n.GetProperty("localName").GetString() == "progress" && attrs.IndexOf("id") is var i && i >= 0 && attrs[i + 1] == id);
        if (inside && n.TryGetProperty("pseudo", out _) is false && attrs.IndexOf("pseudo") is var j && j >= 0 && attrs[j + 1] == "-webkit-progress-value")
            return n.GetProperty("nodeId").GetInt32();
        foreach (var key in new[] { "shadowRoots", "children" })
            if (n.TryGetProperty(key, out var kids))
                foreach (var k in kids.EnumerateArray())
                    if (FindFill(k, id, here) is int found) return found;
        return null;
    }

    private async Task<Dictionary<string, string>> FillByGcsAsync(string id) => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(await Page.EvaluateAsync<string>($$"""
        () => {
          const el = document.getElementById('{{id}}');
          const pseudo = CSS.supports('selector(::-moz-progress-bar)') ? '::-moz-progress-bar' : '::-webkit-progress-value';
          const s = getComputedStyle(el, pseudo);
          const p = getComputedStyle(el);
          return JSON.stringify({ pseudo, background: s.backgroundColor, width: s.width, animationName: s.animationName,
                   animationDuration: s.animationDuration, appearance: p.appearance, track: p.backgroundColor });
        }
        """))!;

    [Fact]
    public async Task Progress_fill_and_sweep_render_and_spinner_spins()
    {
        await MountAsync();
        var det = await FillAsync("det");
        var ind = await FillAsync("ind");
        Console.WriteLine($"[{Browser}] det: {string.Join(", ", det.Select(kv => $"{kv.Key}={kv.Value}"))}");
        Console.WriteLine($"[{Browser}] ind: {string.Join(", ", ind.Select(kv => $"{kv.Key}={kv.Value}"))}");
        Assert.Equal("none", det["appearance"]);
        Assert.NotEqual("rgba(0, 0, 0, 0)", det["background"]);
        Assert.NotEqual(det["track"], det["background"]);
        Assert.Equal("none", det["animationName"]);
        Assert.Equal("r2m-sweep", ind["animationName"]);
        Assert.Equal("1.2s", ind["animationDuration"]);
        Assert.Equal(det["background"], ind["background"]);

        // The sweep moves: two shots of the indeterminate bar differ.
        var bar = Page.Locator("#ind");
        var a = await bar.ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
        await Page.WaitForTimeoutAsync(300);
        var b = await bar.ScreenshotAsync(new() { Animations = ScreenshotAnimations.Allow });
        Assert.False(a.AsSpan().SequenceEqual(b), "indeterminate bar did not move");

        var spin = await Page.Locator("#spin").EvaluateAsync<string>("e => getComputedStyle(e).animationName + ' ' + getComputedStyle(e).animationDuration");
        Assert.Equal("r2m-spin 0.8s", spin);
        await Page.Locator("#proof").ScreenshotAsync(new() { Path = Path.Combine(Shots, $"progress-{Browser}.png"), Animations = ScreenshotAnimations.Allow });
    }

    [Fact]
    public async Task Reduced_motion_slows_both_without_stopping_them()
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await MountAsync();
        var ind = await FillAsync("ind");
        Console.WriteLine($"[{Browser}] reduced ind: {ind["animationName"]} {ind["animationDuration"]}");
        Assert.Equal("r2m-sweep", ind["animationName"]);
        Assert.Equal("3.6s", ind["animationDuration"]);
        var spin = await Page.Locator("#spin").EvaluateAsync<string>("e => getComputedStyle(e).animationName + ' ' + getComputedStyle(e).animationDuration");
        Console.WriteLine($"[{Browser}] reduced spin: {spin}");
        Assert.Equal("r2m-spin 2.4s", spin);
    }
}
