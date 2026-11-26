using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #470: a mark is a button whose aria-expanded follows its card.</summary>
public class PronMarkExpandedProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task A_mark_is_a_dialog_button_whose_aria_expanded_follows_its_card()
    {
        await App.SeedProjectAsync("pron-mark-expanded", "Marks Book", "A. Author");
        var added = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/pron-mark-expanded/pronunciations",
            new { writtenForm = "stormy", spokenForm = "STORM-ee" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        await GotoAppAsync("projects/pron-mark-expanded/book?mode=speakers");
        var mark = Page.GetByRole(AriaRole.Button, new() { Name = "stormy", Exact = true });
        await Expect(mark).ToBeVisibleAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-haspopup", "dialog");
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "false");
        var card = Page.Locator(".pron-card__panel");

        // Focus opens the card at once; Escape closes it and leaves focus on the mark.
        await mark.FocusAsync();
        await Expect(card).ToBeVisibleAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "true");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(card).ToBeHiddenAsync();
        await Expect(mark).ToBeFocusedAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "false");

        // Enter on the focused, closed mark opens the card and moves into it.
        await Page.Keyboard.PressAsync("Enter");
        await Expect(card).ToBeVisibleAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(card.Locator("[data-action=play-sentence]")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "false");

        // Space activates it as Enter does.
        await Page.Keyboard.PressAsync(" ");
        await Expect(card).ToBeVisibleAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "true");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "false");

        // Hover opens it too.
        await mark.HoverAsync();
        await Expect(card).ToBeVisibleAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "true");
        await Page.Mouse.MoveAsync(0, 0);
        await Expect(card).ToBeHiddenAsync();
        await Expect(mark).ToHaveAttributeAsync("aria-expanded", "false");
    }
}
