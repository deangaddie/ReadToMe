using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #512: under forced colours the switch's states differ, the selected segment,
/// active nav item and selected config and Cast rows stand out in <c>Highlight</c>, both progress
/// bars show a fill, the spinner keeps an arc, and dialogs and tooltips have an edge. Chromium
/// emulates <c>forcedColors: 'active'</c>; Firefox launches its own browser with
/// <c>ui.useAccessibilityTheme = 1</c>. Without forced colours the same elements keep their old look.
/// </summary>
public class ForcedColoursProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly bool _firefox = pw.IsFirefox;
    private readonly string _browserName = pw.BrowserName;

    [Fact]
    public async Task State_and_edges_survive_forced_colours()
    {
        await App.SeedProjectAsync("forced", "Forced Colours", "A. Author", "Holmes");
        await GotoAppAsync("projects/forced/cast"); // stages the bundle

        IBrowser? ownBrowser = null;
        IPlaywright? ownPlaywright = null;
        IPage page = Page;
        try
        {
            if (_firefox)
            {
                ownPlaywright = await Playwright.CreateAsync();
                ownBrowser = await ownPlaywright.Firefox.LaunchAsync(new()
                {
                    Headless = Environment.GetEnvironmentVariable("E2E_HEADED") != "1",
                    FirefoxUserPrefs = new Dictionary<string, object> { ["ui.useAccessibilityTheme"] = 1 },
                });
                var ctx = await ownBrowser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 }, BaseURL = App.BaseUrl });
                page = await ctx.NewPageAsync();
            }
            else
            {
                await page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.Active });
            }

            await OpenCastAsync(page, "forced");
            var forced = await page.EvaluateAsync<bool>("() => matchMedia('(forced-colors: active)').matches");
            Log($"{_browserName}: (forced-colors: active) matches = {forced}");
            Assert.True(forced, "forced colours should be on");

            var r = JsonDocument.Parse(await page.EvaluateAsync<string>(MeasureScript)).RootElement;
            Log(r.ToString());
            var sys = r.GetProperty("sys");
            string S(string name) => sys.GetProperty(name).GetString()!;
            string V(string key) => r.GetProperty(key).GetString()!;

            Assert.NotEqual(S("Highlight"), S("Canvas"));
            // Switch: off is a CanvasText-edged track with a CanvasText knob; on is a Highlight track
            // with a HighlightText knob.
            Assert.Equal(S("CanvasText"), V("switchOffBorder"));
            Assert.Equal("solid", V("switchOffBorderStyle"));
            Assert.Equal(S("CanvasText"), V("switchOffKnob"));
            Assert.Equal(S("Highlight"), V("switchOnTrack"));
            Assert.Equal(S("HighlightText"), V("switchOnKnob"));
            Assert.NotEqual(V("switchOffTrack"), V("switchOnTrack"));
            Assert.NotEqual(V("switchOffKnob"), V("switchOnKnob"));
            // Segmented, nav item: Highlight fill with HighlightText.
            Assert.Equal(S("Highlight"), V("segmentOnBg"));
            Assert.Equal(S("HighlightText"), V("segmentOnFg"));
            Assert.NotEqual(V("segmentOnBg"), V("segmentOffBg"));
            Assert.Equal(S("Highlight"), V("navBg"));
            Assert.Equal(S("HighlightText"), V("navFg"));
            // Selected rows: a Highlight outline.
            Assert.Equal("solid", V("configRowOutlineStyle"));
            Assert.Equal(S("Highlight"), V("configRowOutline"));
            Assert.Equal("solid", V("castRowOutlineStyle"));
            Assert.Equal(S("Highlight"), V("castRowOutline"));
            // Progress: a CanvasText outline on the track and a Highlight fill, determinate and
            // indeterminate; the indeterminate band still moves. The fill is a pseudo-element whose
            // computed style neither engine reports, so its pixels are read from element shots.
            Assert.Equal("solid", V("progressOutlineStyle"));
            Assert.Equal(S("CanvasText"), V("progressOutline"));
            var highlight = S("Highlight");
            var det = await HighlightRunAsync(page, "#proof-det", highlight);
            Log($"determinate: {det}");
            Assert.True(det.Count > 50, $"the determinate fill should be Highlight pixels: {det}");
            var band1 = await HighlightRunAsync(page, "#proof-ind", highlight);
            await Task.Delay(250);
            var band2 = await HighlightRunAsync(page, "#proof-ind", highlight);
            Log($"indeterminate: {band1} then {band2}");
            Assert.True(band1.Count > 0 || band2.Count > 0, "the indeterminate band should be Highlight pixels");
            Assert.NotEqual(band1, band2);
            // Spinner: a CanvasText arc over a GrayText track.
            Assert.Equal(S("CanvasText"), V("spinnerArc"));
            Assert.Equal(S("GrayText"), V("spinnerTrack"));
            // Dialog and tooltip edges.
            Assert.Equal("solid", V("dialogBorderStyle"));
            Assert.Equal(S("CanvasText"), V("dialogBorder"));
            Assert.Equal("solid", V("tooltipBorderStyle"));
            Assert.Equal(S("CanvasText"), V("tooltipBorder"));

            await ShotAsync(page, "forced");
        }
        finally
        {
            if (ownBrowser is not null) await ownBrowser.DisposeAsync();
            ownPlaywright?.Dispose();
        }
    }

    [Fact]
    public async Task Normal_colours_keep_their_old_look()
    {
        await App.SeedProjectAsync("normal", "Normal Colours", "A. Author", "Holmes");
        await GotoAppAsync("projects/normal/cast");
        await OpenCastAsync(Page, "normal");
        Assert.False(await Page.EvaluateAsync<bool>("() => matchMedia('(forced-colors: active)').matches"));

        var r = JsonDocument.Parse(await Page.EvaluateAsync<string>(MeasureScript)).RootElement;
        Log(r.ToString());
        string V(string key) => r.GetProperty(key).GetString()!;
        Assert.Equal("none", V("switchOffBorderStyle"));
        Assert.Equal("none", V("dialogBorderStyle"));
        Assert.Equal("none", V("tooltipBorderStyle"));
        Assert.Equal("none", V("progressOutlineStyle"));
        Assert.Equal("none", V("configRowOutlineStyle"));
        Assert.Equal("none", V("castRowOutlineStyle"));
        await ShotAsync(Page, "normal");
    }

    private static async Task OpenCastAsync(IPage page, string folder)
    {
        await page.GotoAsync(AppPath($"projects/{folder}/cast"));
        await Assertions.Expect(page.Locator("app-root[data-live-state='connected']")).ToBeAttachedAsync(new() { Timeout = 15_000 });
        var row = page.Locator(".cast__row").First;
        await row.ClickAsync();
        await Assertions.Expect(page.Locator(".cast__row--selected")).ToBeVisibleAsync();
        // Move focus off the row, so its own focus ring doesn't stand in for the selection.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
    }

    private async Task ShotAsync(IPage page, string mode)
    {
        if (Environment.GetEnvironmentVariable("R2M_PROOF_SHOTS") is { Length: > 0 } dir)
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"r2m-512-{_browserName}-{mode}.png") });
    }

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private readonly record struct Run(int Count, int First, int Last);

    // The Highlight pixels along the middle row of an element's shot, decoded in the page.
    private static async Task<Run> HighlightRunAsync(IPage page, string selector, string target)
    {
        var png = await page.Locator(selector).ScreenshotAsync();
        var r = await page.EvaluateAsync<JsonElement>("""
            async ([b64, target]) => {
              const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
              const bmp = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
              const c = document.createElement('canvas');
              c.width = bmp.width;
              c.height = bmp.height;
              const g = c.getContext('2d');
              g.drawImage(bmp, 0, 0);
              const [tr, tg, tb] = target.match(/\d+/g).map(Number);
              const y = Math.floor(bmp.height / 2);
              const px = g.getImageData(0, y, bmp.width, 1).data;
              let count = 0, first = -1, last = -1;
              for (let x = 0; x < bmp.width; x++) {
                const i = x * 4;
                if (Math.abs(px[i] - tr) + Math.abs(px[i + 1] - tg) + Math.abs(px[i + 2] - tb) < 24) {
                  count++;
                  if (first < 0) first = x;
                  last = x;
                }
              }
              return { count, first, last };
            }
            """, new object[] { Convert.ToBase64String(png), target });
        return new(r.GetProperty("count").GetInt32(), r.GetProperty("first").GetInt32(), r.GetProperty("last").GetInt32());
    }

    // Builds the primitives with DOM calls (Trusted Types forbids innerHTML) in a panel over the
    // Cast page, opens a modal dialog, and reads computed colours, normalised through a canvas.
    private const string MeasureScript = """
        () => {
          const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
          const rgb = (c) => {
            ctx.clearRect(0, 0, 1, 1);
            ctx.fillStyle = '#000';
            ctx.fillStyle = c;
            ctx.fillRect(0, 0, 1, 1);
            const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data;
            return `rgba(${r},${g},${b},${a})`;
          };
          const el = (tag, cls, props = {}) => Object.assign(document.createElement(tag), cls ? { className: cls } : {}, props);
          const cs = (n, p) => getComputedStyle(n, p);
          const box = el('div');
          box.style.cssText = 'position:fixed;left:16px;bottom:160px;z-index:5;display:flex;flex-wrap:wrap;gap:16px;align-items:center;padding:16px;max-width:1000px';
          document.body.append(box);

          const sys = {};
          for (const name of ['Canvas', 'CanvasText', 'Highlight', 'HighlightText', 'GrayText']) {
            const probe = el('span');
            probe.style.backgroundColor = name;
            box.append(probe);
            sys[name] = rgb(cs(probe).backgroundColor);
            probe.remove();
          }

          const sw = (checked, text) => {
            const label = el('label', 'r2m-switch');
            const input = el('input', '', { type: 'checkbox', checked });
            input.setAttribute('role', 'switch');
            label.append(input, text);
            box.append(label);
            return input;
          };
          const off = sw(false, 'Off');
          const on = sw(true, 'On');

          const seg = el('fieldset', 'r2m-segmented');
          seg.setAttribute('role', 'radiogroup');
          const segLabels = ['One', 'Two'].map((t, i) => {
            const l = el('label');
            l.append(el('input', '', { type: 'radio', name: 'seg', checked: i === 0 }), t);
            seg.append(l);
            return l;
          });
          box.append(seg);

          const list = el('ul', 'r2m-config-list__rows');
          list.style.width = '240px';
          const row = el('li', 'r2m-config-list__row r2m-config-list__row--selected');
          row.append(el('button', 'r2m-config-list__main', { type: 'button', textContent: 'Selected config' }));
          list.append(row);
          box.append(list);

          const det = el('progress', '', { max: 1, value: 0.6, id: 'proof-det' });
          const ind = el('progress', '', { id: 'proof-ind' });
          for (const p of [det, ind]) {
            p.style.width = '200px';
            box.append(p);
          }
          const spin = el('span', 'r2m-spinner');
          box.append(spin);

          const tip = el('div', 'r2m-tooltip', { textContent: 'A tooltip' });
          tip.style.position = 'static';
          box.append(tip);

          const dialog = el('dialog', 'r2m-dialog');
          dialog.append(el('h2', 'r2m-dialog__title', { textContent: 'A dialog' }));
          dialog.style.margin = 'auto 24px 24px auto';
          document.body.append(dialog);
          dialog.show();

          const nav = document.querySelector('.shell__nav-item--active');
          const cast = document.querySelector('.cast__row--selected');
          return JSON.stringify({
            sys,
            switchOffTrack: rgb(cs(off).backgroundColor),
            switchOffBorder: rgb(cs(off).borderTopColor),
            switchOffBorderStyle: cs(off).borderTopStyle,
            switchOffKnob: rgb(cs(off, '::before').backgroundColor),
            switchOnTrack: rgb(cs(on).backgroundColor),
            switchOnKnob: rgb(cs(on, '::before').backgroundColor),
            segmentOnBg: rgb(cs(segLabels[0]).backgroundColor),
            segmentOnFg: rgb(cs(segLabels[0]).color),
            segmentOffBg: rgb(cs(segLabels[1]).backgroundColor),
            navBg: nav ? rgb(cs(nav).backgroundColor) : 'missing',
            navFg: nav ? rgb(cs(nav).color) : 'missing',
            configRowOutline: rgb(cs(row).outlineColor),
            configRowOutlineStyle: cs(row).outlineStyle,
            castRowOutline: cast ? rgb(cs(cast).outlineColor) : 'missing',
            castRowOutlineStyle: cast ? cs(cast).outlineStyle : 'missing',
            progressOutline: rgb(cs(det).outlineColor),
            progressOutlineStyle: cs(det).outlineStyle,
            spinnerArc: rgb(cs(spin).borderTopColor),
            spinnerTrack: rgb(cs(spin).borderRightColor),
            dialogBorder: rgb(cs(dialog).borderTopColor),
            dialogBorderStyle: cs(dialog).borderTopStyle,
            tooltipBorder: rgb(cs(tip).borderTopColor),
            tooltipBorderStyle: cs(tip).borderTopStyle,
          });
        }
        """;
}
