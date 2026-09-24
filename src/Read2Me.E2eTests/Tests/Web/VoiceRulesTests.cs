using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Voice rules section of the cast page (Angular ticket 17): add a "from here on" rule through
/// the cascading dialog and watch the preview flip, reorder and delete rules, and see a rule whose
/// chapter was deleted flagged as dangling. Every step is also asserted against the host reads the
/// Blazor tab renders from.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceRulesTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private async Task<Guid> RunAsync(string folder, object command)
    {
        var response = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{folder}/commands", command);
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("newEntityId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : Guid.Empty;
    }

    private async Task<List<JsonElement>> RulesAsync(string folder, Guid characterId) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/projects/{folder}/characters/{characterId}/voice-rules"))
            .RootElement.EnumerateArray().ToList();

    private async Task<string[]> PreviewAsync(string folder, Guid characterId) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/projects/{folder}/characters/{characterId}/voice-rules/preview"))
            .RootElement.EnumerateArray().Select(r => r.GetProperty("voiceName").GetString() ?? "").ToArray();

    /// <summary>Opens a Material select inside <paramref name="scope"/> and picks the option with <paramref name="text"/>.</summary>
    private async Task ChooseAsync(ILocator scope, string field, string text)
    {
        await scope.Locator($"mat-select[data-field='{field}']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = text }).First.ClickAsync();
    }

    [Fact]
    public async Task Add_from_chapter_onward_rule_flips_the_preview_then_move_and_delete()
    {
        const string folder = "web-voice-rules";
        var book = await App.SeedMultiChapterProjectAsync(folder, "Rules Book", "A. Author");
        var alice = book.CharacterId("Alice");
        await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice A", isGenerated = true });
        await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice B", isGenerated = true });

        await GotoAppAsync($"/app/projects/{folder}/cast/{alice}");
        var section = Page.Locator("app-voice-rules-section");
        await Expect(section).ToBeVisibleAsync();

        // The first voice's default rule, and every chapter resolving to it.
        var rows = section.Locator("li.voice-rules__row");
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows.First).ToContainTextAsync("Default → Voice A");
        await Expect(rows.First.Locator("[data-action='delete-rule']")).ToHaveCountAsync(0);
        var preview = section.Locator("table.voice-rules__preview tbody tr");
        await Expect(preview).ToHaveCountAsync(5);
        await Expect(preview.Nth(2)).ToContainTextAsync("Chapter 3");
        await Expect(preview.Nth(2).Locator("td").Nth(1)).ToHaveTextAsync("Voice A");

        // Add "From Chapter 3 onward → Voice B" through the cascading dialog.
        await section.Locator("[data-action='add-rule']").ClickAsync();
        var dialog = Page.Locator("app-add-voice-rule-dialog");
        await Expect(dialog.Locator("[data-action='add']")).ToBeDisabledAsync();
        await ChooseAsync(dialog, "voice", "Voice B");
        await Expect(dialog.Locator("mat-select[data-field='part']")).ToHaveCountAsync(0);
        await ChooseAsync(dialog, "volume", "v1");
        await Expect(dialog.Locator("[data-action='add']")).ToBeEnabledAsync();
        await ChooseAsync(dialog, "part", "Untitled");
        await ChooseAsync(dialog, "chapter", "Chapter 3");
        await Expect(dialog.Locator("mat-select[data-field='paragraph']")).ToBeVisibleAsync();
        await dialog.Locator("[data-action='add']").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        await Expect(rows).ToHaveCountAsync(2);
        await Expect(rows.Nth(1)).ToContainTextAsync("From Chapter Chapter 3 onward → Voice B");
        await Expect(rows.Nth(1).Locator("[data-action='move-up']")).ToBeDisabledAsync();
        await Expect(rows.Nth(1).Locator("[data-action='move-down']")).ToBeDisabledAsync();
        await Expect(preview.Nth(1).Locator("td").Nth(1)).ToHaveTextAsync("Voice A");
        await Expect(preview.Nth(2).Locator("td").Nth(1)).ToHaveTextAsync("Voice B");
        await Expect(preview.Nth(4).Locator("td").Nth(1)).ToHaveTextAsync("Voice B");
        Assert.Equal(["Voice A", "Voice A", "Voice B", "Voice B", "Voice B"], await PreviewAsync(folder, alice));
        var onwardId = (await rows.Nth(1).GetAttributeAsync("data-rule-id"))!;

        // A second, "just this node" rule for chapter 4 back to Voice A: later, so it wins there.
        await section.Locator("[data-action='add-rule']").ClickAsync();
        dialog = Page.Locator("app-add-voice-rule-dialog");
        await ChooseAsync(dialog, "voice", "Voice A");
        await dialog.Locator("mat-radio-button", new() { HasText = "Just this node" }).ClickAsync();
        await ChooseAsync(dialog, "volume", "v1");
        await ChooseAsync(dialog, "part", "Untitled");
        await ChooseAsync(dialog, "chapter", "Chapter 4");
        await dialog.Locator("[data-action='add']").ClickAsync();
        await Expect(rows).ToHaveCountAsync(3);
        await Expect(rows.Nth(2)).ToContainTextAsync("Chapter Chapter 4 → Voice A");
        await Expect(preview.Nth(3).Locator("td").Nth(1)).ToHaveTextAsync("Voice A");
        var singleId = (await rows.Nth(2).GetAttributeAsync("data-rule-id"))!;

        // Move it above the onward rule: the onward rule now wins on chapter 4 too.
        await Expect(rows.Nth(2).Locator("[data-action='move-up']")).ToBeEnabledAsync();
        await Expect(rows.Nth(2).Locator("[data-action='move-down']")).ToBeDisabledAsync();
        await rows.Nth(2).Locator("[data-action='move-up']").ClickAsync();
        await Expect(rows.Nth(1)).ToHaveAttributeAsync("data-rule-id", singleId);
        await Expect(rows.Nth(2)).ToHaveAttributeAsync("data-rule-id", onwardId);
        await Expect(preview.Nth(3).Locator("td").Nth(1)).ToHaveTextAsync("Voice B");
        Assert.Equal([singleId, onwardId],
            (await RulesAsync(folder, alice)).Skip(1).Select(r => r.GetProperty("ruleId").GetString()!).ToArray());

        // Delete the onward rule: everything returns to the default except chapter 4's own rule.
        await rows.Nth(2).Locator("[data-action='delete-rule']").ClickAsync();
        var confirm = Page.Locator("r2m-confirm-dialog");
        await Expect(confirm).ToContainTextAsync("Chapter 3 onward");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(preview.Nth(2).Locator("td").Nth(1)).ToHaveTextAsync("Voice A");
        Assert.Equal(["Voice A", "Voice A", "Voice A", "Voice A", "Voice A"], await PreviewAsync(folder, alice));
        Assert.Equal(2, (await RulesAsync(folder, alice)).Count);
    }

    [Fact]
    public async Task Rule_anchored_to_a_deleted_chapter_shows_the_dangling_warning()
    {
        const string folder = "web-voice-rules-dangling";
        var book = await App.SeedMultiChapterProjectAsync(folder, "Dangling Book", "A. Author", chapters: 3);
        var alice = book.CharacterId("Alice");
        await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice A", isGenerated = true });
        var voiceB = await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice B", isGenerated = true });
        var chapter3 = book.ChapterId("Chapter 3");
        await RunAsync(folder, new
        {
            type = "CreateVoiceRule", characterId = alice, voiceId = voiceB,
            fromLevel = "Chapter", fromNodeId = chapter3, toLevel = "Chapter", toNodeId = chapter3,
        });

        await GotoAppAsync($"/app/projects/{folder}/cast/{alice}");
        var section = Page.Locator("app-voice-rules-section");
        var rows = section.Locator("li.voice-rules__row");
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(rows.Nth(1)).ToContainTextAsync("Chapter Chapter 3 → Voice B");
        await Expect(rows.Nth(1).Locator("[data-role='dangling']")).ToHaveCountAsync(0);
        var preview = section.Locator("table.voice-rules__preview tbody tr");
        await Expect(preview).ToHaveCountAsync(3);
        await Expect(preview.Nth(2).Locator("td").Nth(1)).ToHaveTextAsync("Voice B");

        // The chapter goes away elsewhere (the API here; the Blazor tab in practice): the Structure
        // receipt reloads the section, the rule reads as missing and the preview loses the row.
        await RunAsync(folder, new { type = "DeleteChapter", chapterId = chapter3 });

        await Expect(rows.Nth(1).Locator("[data-role='dangling']")).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Expect(rows.Nth(1)).ToContainTextAsync("(missing node) → Voice B");
        await Expect(preview).ToHaveCountAsync(2);
        var rule = (await RulesAsync(folder, alice)).Single(r => !r.GetProperty("isDefault").GetBoolean());
        Assert.True(rule.GetProperty("fromDangling").GetBoolean());
    }
}
