using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #475: the inline edit gives focus back after a key, names its button with the
/// text it shows, leaves a composing Enter/Escape alone, shows its pencil where nothing hovers and
/// draws a focus ring that hover does not. Deleted before merge.
/// </summary>
public class InlineEditProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly bool _firefox = pw.IsFirefox;

    private const string Card = "r2m-voice-card";
    private const string Display = $"{Card} .voice-card__name .r2m-inline-edit__display";
    private const string Input = $"{Card} .voice-card__name .r2m-inline-edit__input";

    /// <summary>The focused element is the voice name's display button; its accessible name.</summary>
    private const string FocusedName =
        "() => document.activeElement?.matches('.voice-card__name .r2m-inline-edit__display') " +
        "? document.activeElement.getAttribute('aria-label') : null";

    private async Task<string> OpenVoiceAsync()
    {
        var folder = $"inline-edit-{Guid.NewGuid():N}";
        var book = await App.SeedProjectAsync(folder, "Pride and Prejudice", "Jane Austen", characterName: "Mary Bennet");
        var mary = book.CharacterId("Mary Bennet");
        await App.SeedEditableVoiceAsync(folder, mary, "Mary Bennet");
        var path = $"projects/{folder}/cast/{mary}";
        await GotoAppAsync(path);
        await Expect(Page.Locator(Display)).ToBeVisibleAsync();
        return path;
    }

    [Fact]
    public async Task Rename_with_Enter_keeps_focus_and_names_the_new_value()
    {
        await OpenVoiceAsync();
        var display = Page.Locator(Display);
        await Expect(display).ToHaveAccessibleNameAsync("Edit Voice name: Mary Bennet");

        await display.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Page.Locator(Input).FillAsync("Mary, at the pianoforte");
        var saved = Page.WaitForResponseAsync(r => r.Url.EndsWith("/commands") && r.Request.Method == "POST");
        await Page.Keyboard.PressAsync("Enter");
        Assert.Equal(200, (await saved).Status);

        await Expect(display).ToHaveAccessibleNameAsync("Edit Voice name: Mary, at the pianoforte");
        await Expect(display).ToContainTextAsync("Mary, at the pianoforte");
        // After the reload landed too: nothing swapped the button out from under the focus.
        await Expect(Page.Locator($"{Card}[aria-busy]")).ToHaveCountAsync(0);
        Assert.Equal("Edit Voice name: Mary, at the pianoforte", await Page.EvaluateAsync<string?>(FocusedName));
        // Focus draws a ring the keyboard user can see; the card never toggled.
        await Expect(display).ToHaveCSSAsync("outline-style", "solid");
        await Expect(display).ToHaveCSSAsync("outline-width", "2px");
        await Expect(display).ToHaveCSSAsync("outline-offset", "-2px");
        await Expect(Page.Locator($"{Card} details.voice-card__panel")).Not.ToHaveAttributeAsync("open", "");

        // Escape gives focus back as well.
        await Page.Keyboard.PressAsync("Enter");
        await Page.Locator(Input).FillAsync("Thrown away");
        await Page.Keyboard.PressAsync("Escape");
        Assert.Equal("Edit Voice name: Mary, at the pianoforte", await Page.EvaluateAsync<string?>(FocusedName));

        // A blur-save leaves focus where Tab took it.
        await Page.Keyboard.PressAsync("Enter");
        await Page.Locator(Input).FillAsync("Mary Bennet");
        await Page.Keyboard.PressAsync("Tab");
        await Expect(display).ToHaveAccessibleNameAsync("Edit Voice name: Mary Bennet");
        await Expect(Page.Locator($"{Card}[aria-busy]")).ToHaveCountAsync(0);
        Assert.Null(await Page.EvaluateAsync<string?>(FocusedName));
        Assert.NotEqual("BODY", await Page.EvaluateAsync<string>("() => document.activeElement.tagName"));
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Escape")]
    public async Task A_composing_key_neither_saves_nor_aborts(string key)
    {
        await OpenVoiceAsync();
        var writes = new List<string>();
        Page.Request += (_, r) => { if (r.Method != "GET" && r.Url.Contains("/api/projects/")) writes.Add(r.Url); };

        await Page.Locator(Display).ClickAsync();
        await Page.Locator(Input).FillAsync("めありー");
        await Page.Locator(Input).EvaluateAsync(
            "(el, key) => el.dispatchEvent(new KeyboardEvent('keydown', { key, isComposing: true, bubbles: true, cancelable: true }))",
            key);
        // A beat for a request to leave, had one been made.
        await Page.WaitForTimeoutAsync(500);

        await Expect(Page.Locator(Input)).ToHaveValueAsync("めありー");
        await Expect(Page.Locator(Input)).ToBeFocusedAsync();
        Assert.Empty(writes);
    }

    [Fact]
    public async Task Hover_tints_without_a_ring_and_hides_the_pencil_elsewhere()
    {
        await OpenVoiceAsync();
        var display = Page.Locator(Display);
        var pencil = display.Locator(".r2m-inline-edit__pencil");
        Assert.False(await Page.EvaluateAsync<bool>("() => matchMedia('(hover: none)').matches"));
        await Expect(pencil).ToHaveCSSAsync("opacity", "0");

        await display.HoverAsync();

        await Expect(pencil).ToHaveCSSAsync("opacity", "1");
        await Expect(display).ToHaveCSSAsync("outline-style", "none");
        await Expect(display).Not.ToHaveCSSAsync("background-color", "rgba(0, 0, 0, 0)");
    }

    [Fact]
    public async Task Where_nothing_hovers_the_pencil_shows()
    {
        var path = await OpenVoiceAsync();
        // A touch device: Firefox by its pointer capabilities (1 = coarse, no hover), Chromium by a
        // mobile touch context.
        using var playwright = await Playwright.CreateAsync();
        await using var browser = _firefox
            ? await playwright.Firefox.LaunchAsync(new()
            {
                FirefoxUserPrefs = new Dictionary<string, object>
                {
                    ["ui.primaryPointerCapabilities"] = 1,
                    ["ui.allPointerCapabilities"] = 1,
                },
            })
            : await playwright.Chromium.LaunchAsync();
        await using var context = await browser.NewContextAsync(_firefox
            ? new() { BaseURL = App.BaseUrl }
            : new() { BaseURL = App.BaseUrl, HasTouch = true, IsMobile = true, ViewportSize = new() { Width = 1024, Height = 768 } });
        var touch = await context.NewPageAsync();
        await touch.GotoAsync(AppPath(path));
        var pencil = touch.Locator($"{Display} .r2m-inline-edit__pencil");
        await Expect(pencil).ToBeAttachedAsync(new() { Timeout = 15_000 });

        Assert.True(await touch.EvaluateAsync<bool>("() => matchMedia('(hover: none)').matches"));
        await Expect(pencil).ToHaveCSSAsync("opacity", "1");
    }
}
