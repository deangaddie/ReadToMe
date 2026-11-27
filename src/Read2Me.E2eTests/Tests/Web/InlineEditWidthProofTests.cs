using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #534: an Inline edit's value is cut only when it is wider than the room, a
/// click beside a short voice name opens the card, and the other uses keep their place. Writes what
/// it read to <c>R2M_PROOF_OUT</c> (label <c>R2M_PROOF_LABEL</c>). Deleted before merge.
/// </summary>
public class InlineEditWidthProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly string _browser = pw.IsFirefox ? "firefox" : "chromium";
    private static string OutDir => Environment.GetEnvironmentVariable("R2M_PROOF_OUT") ?? Path.GetTempPath();
    private static string Label => Environment.GetEnvironmentVariable("R2M_PROOF_LABEL") ?? "proof";
    private static readonly bool Strict = Environment.GetEnvironmentVariable("R2M_PROOF_STRICT") == "1";

    private const string LongName =
        "Alice at the very end of the long hall where the rabbit went down the hole and kept on falling";

    /// <summary>The display's text box against what its text needs, and the boxes around it.</summary>
    private static async Task<string> MeasureAsync(ILocator edit, ILocator bound) => await edit.EvaluateAsync<string>(
        """
        (edit, bound) => {
          const text = edit.querySelector('.r2m-inline-edit__text');
          const button = edit.querySelector('.r2m-inline-edit__display');
          const b = bound.getBoundingClientRect(), h = edit.getBoundingClientRect(), d = button.getBoundingClientRect();
          const r = v => Math.round(v * 10) / 10;
          return `"${text.textContent}" text ${text.clientWidth}/${text.scrollWidth} cut=${text.scrollWidth > text.clientWidth}`
            + ` host x ${r(h.x - b.x)} w ${r(h.width)} | button x ${r(d.x - b.x)} w ${r(d.width)} right ${r(d.right - b.x)}`
            + ` | bound w ${r(b.width)} overflows=${d.right > b.right + 4.5}`;
        }
        """, await bound.ElementHandleAsync());

    private static Task<bool> CutAsync(ILocator edit) => edit.EvaluateAsync<bool>(
        "e => { const t = e.querySelector('.r2m-inline-edit__text'); return t.scrollWidth > t.clientWidth; }");

    // Needs a real browser: truncation and the hit test are the engine's layout.
    [Fact]
    public async Task Values_are_cut_only_past_the_room_and_a_click_beside_a_short_name_opens_the_card()
    {
        var log = new StringBuilder();
        var folder = $"inline-width-{Guid.NewGuid():N}";
        var book = await App.SeedProjectAsync(folder, "Alice's Adventures in Wonderland", "Lewis Carroll",
            characterName: "Alice");
        var alice = book.CharacterId("Alice");
        await App.SeedEditableVoiceAsync(folder, alice, "Main");
        using var http = new HttpClient { BaseAddress = new Uri(App.BaseUrl) };
        async Task<string> VoiceAsync(string name, string? description)
        {
            var created = await http.PostAsJsonAsync($"/api/projects/{folder}/commands",
                new { type = "CreateVoice", characterId = alice, name, isGenerated = false });
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("newEntityId").GetString()!;
            if (description is not null)
                (await http.PostAsJsonAsync($"/api/projects/{folder}/commands",
                    new { type = "UpdateVoice", voiceId = id, name, description })).EnsureSuccessStatusCode();
            return id;
        }
        var whisper = await VoiceAsync("Whisper", null);
        var described = await VoiceAsync("Quiet", "A soft, low voice for the garden scenes, spoken close to the ear");
        var longId = await VoiceAsync(LongName, null);

        await GotoAppAsync($"projects/{folder}/cast/{alice}");
        ILocator Card(string id) => Page.Locator($"r2m-voice-card[data-voice-id='{id}']");
        await Expect(Card(longId).Locator(".voice-card__header")).ToBeVisibleAsync();

        log.AppendLine($"=== {_browser} ===");
        foreach (var (name, id) in new[] { ("whisper", whisper), ("described", described), ("long", longId) })
        {
            var card = Card(id);
            log.AppendLine($"voice {name}: " + await MeasureAsync(card.Locator("r2m-inline-edit"), card.Locator(".voice-card__header")));
        }
        await Card(whisper).ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"{Label}-{_browser}-whisper.png") });
        await Card(described).ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"{Label}-{_browser}-described.png") });
        await Card(longId).ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"{Label}-{_browser}-long.png") });

        var character = Page.Locator("r2m-character-detail r2m-inline-edit.character-detail__name");
        log.AppendLine("character: " + await MeasureAsync(character, Page.Locator(".character-detail__header")));

        // A click just right of a short name, in a card whose description is wider.
        var describedCard = Card(described);
        var display = (await describedCard.Locator(".r2m-inline-edit__display").BoundingBoxAsync())!;
        var desc = (await describedCard.Locator(".voice-card__description").BoundingBoxAsync())!;
        await Page.Mouse.ClickAsync(display.X + display.Width + 20, display.Y + display.Height / 2);
        var expanded = await describedCard.Locator(".voice-card__toggle").GetAttributeAsync("aria-expanded");
        var editing = await describedCard.Locator(".r2m-inline-edit__input").CountAsync();
        log.AppendLine($"click beside the short name (description right {Math.Round(desc.X + desc.Width - display.X)} px past its left): expanded={expanded} editing={editing}");

        var cuts = (await CutAsync(Card(whisper).Locator("r2m-inline-edit")), await CutAsync(Card(described).Locator("r2m-inline-edit")), await CutAsync(Card(longId).Locator("r2m-inline-edit")));

        await GotoAppAsync($"projects/{folder}");
        var panel = Page.Locator("r2m-project-details-panel");
        await Expect(panel.Locator("r2m-inline-edit.details__title")).ToBeVisibleAsync();
        log.AppendLine("title: " + await MeasureAsync(panel.Locator("r2m-inline-edit.details__title"), panel));
        log.AppendLine("author: " + await MeasureAsync(panel.Locator("r2m-inline-edit.details__author"), panel));
        await panel.ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"{Label}-{_browser}-details.png") });

        await File.WriteAllTextAsync(Path.Combine(OutDir, $"{Label}-{_browser}.txt"), log.ToString());

        if (!Strict) return;
        Assert.Equal((false, false, true), cuts);
        Assert.Equal("true", expanded);
        Assert.Equal(0, editing);
    }
}
