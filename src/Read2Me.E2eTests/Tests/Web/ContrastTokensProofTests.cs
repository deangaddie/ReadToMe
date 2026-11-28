using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #511: in the built-in Light and Dark themes, control boundaries reach 3:1
/// against the surface they sit on and the ok chip's text reaches 4.5:1 on its background. Reads
/// computed colours in the browser and computes the WCAG ratios here.
/// </summary>
public class ContrastTokensProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Control_edges_reach_3_to_1_and_the_ok_chip_reaches_4_5_to_1(string themeName)
    {
        await SelectThemeAsync(themeName);
        if (Environment.GetEnvironmentVariable("R2M_PROOF_SHOTS") is { Length: > 0 } shots)
        {
            await GotoAppAsync("settings/audio");
            await Expect(Page.Locator(".r2m-switch").First).ToBeVisibleAsync();
            await Page.ScreenshotAsync(new() { Path = Path.Combine(shots, $"audio-settings-{themeName.ToLowerInvariant()}.png") });
        }
        await GotoAppAsync("settings/themes");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", themeName == "Dark" ? "dark" : "light");

        var json = JsonDocument.Parse(await Page.EvaluateAsync<string>(MeasureScript)).RootElement;
        var lines = new List<string>();
        var failures = new List<string>();
        foreach (var row in json.EnumerateArray())
        {
            var what = row.GetProperty("what").GetString()!;
            var fg = Rgb(row.GetProperty("fg"));
            var bg = Rgb(row.GetProperty("bg"));
            var min = row.GetProperty("min").GetDouble();
            var ratio = Contrast(fg, bg);
            var line = $"{themeName,-5} {what,-40} {Hex(fg)} on {Hex(bg)} = {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1 (min {min})";
            lines.Add(line);
            if (ratio < min) failures.Add(line);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join('\n', lines));
        Assert.True(lines.Count >= 11, $"expected every measurement, got {lines.Count}");
        Assert.True(failures.Count == 0, "below the minimum:\n" + string.Join('\n', failures));
    }

    private async Task SelectThemeAsync(string name)
    {
        var themes = await Http.GetFromJsonAsync<JsonElement>($"{App.BaseUrl}/api/settings/themes");
        var id = themes.EnumerateArray()
            .First(t => t.GetProperty("isBuiltIn").GetBoolean() && t.GetProperty("name").GetString() == name)
            .GetProperty("id").GetInt32();
        var response = await Http.PutAsJsonAsync($"{App.BaseUrl}/api/settings/themes/selection",
            new { selectedThemeId = id, followSystemPreference = false });
        response.EnsureSuccessStatusCode();
    }

    // Builds each primitive with DOM calls (Trusted Types forbids innerHTML) on the page surface and
    // on --r2m-surface-container, then resolves every computed colour to sRGB through a canvas.
    private const string MeasureScript = """
        () => {
          const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
          const rgb = (c) => {
            ctx.clearRect(0, 0, 1, 1);
            ctx.fillStyle = '#000';
            ctx.fillStyle = c;
            ctx.fillRect(0, 0, 1, 1);
            const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data;
            return { r, g, b, a };
          };
          const surfaceOf = (el) => {
            for (let n = el; n; n = n.parentElement) {
              const c = rgb(getComputedStyle(n).backgroundColor);
              if (c.a === 255) return c;
            }
            return rgb(getComputedStyle(document.documentElement).backgroundColor);
          };
          const el = (tag, cls, props = {}) => Object.assign(document.createElement(tag), cls ? { className: cls } : {}, props);
          const out = [];
          const host = document.querySelector('.shell__body') ?? document.body;
          for (const [label, bgVar] of [['page', null], ['container', '--r2m-surface-container']]) {
            const box = el('div');
            box.style.padding = '16px';
            if (bgVar) box.style.background = `var(${bgVar})`;
            host.prepend(box);

            const field = el('div', 'r2m-field');
            const input = el('input', '', { type: 'text' });
            field.append(input);
            const stroked = el('button', 'r2m-button r2m-button--stroked', { type: 'button', textContent: 'Stroked' });
            const seg = el('div', 'r2m-segmented', { role: 'radiogroup' });
            for (const t of ['One', 'Two']) {
              const l = el('label');
              l.append(el('input', '', { type: 'radio', name: `seg-${label}` }), t);
              seg.append(l);
            }
            const sw = el('label', 'r2m-switch');
            const track = el('input', '', { type: 'checkbox' });
            track.setAttribute('role', 'switch');
            sw.append(track, 'Off');
            box.append(field, stroked, seg, sw);

            const surface = surfaceOf(box);
            const border = (n) => rgb(getComputedStyle(n).borderTopColor);
            out.push({ what: `${label}: text field border`, fg: border(input), bg: surface, min: 3 });
            out.push({ what: `${label}: stroked button border`, fg: border(stroked), bg: surface, min: 3 });
            out.push({ what: `${label}: segmented edge`, fg: border(seg), bg: surface, min: 3 });
            const trackColour = rgb(getComputedStyle(track).backgroundColor);
            out.push({ what: `${label}: unchecked switch track`, fg: trackColour, bg: surface, min: 3 });
            out.push({ what: `${label}: unchecked switch knob on track`, fg: rgb(getComputedStyle(track, '::before').backgroundColor), bg: trackColour, min: 3 });
          }
          const chipBox = el('div');
          host.prepend(chipBox);
          const chip = el('span', 'r2m-status-chip r2m-status-chip--ok', { textContent: 'Ready' });
          chipBox.append(chip);
          const cs = getComputedStyle(chip);
          out.push({ what: 'ok chip text', fg: rgb(cs.color), bg: rgb(cs.backgroundColor), min: 4.5 });
          return JSON.stringify(out);
        }
        """;

    private static (int R, int G, int B) Rgb(JsonElement c) =>
        (c.GetProperty("r").GetInt32(), c.GetProperty("g").GetInt32(), c.GetProperty("b").GetInt32());

    private static string Hex((int R, int G, int B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
