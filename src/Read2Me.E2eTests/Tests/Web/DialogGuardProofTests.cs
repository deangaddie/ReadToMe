using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #464: dialog names and the canClose guard in a real engine.</summary>
public class DialogGuardProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string LlmPlan =
        """
        { "reasoning": "capitalise every chapter title", "supported": true, "unsupported_reason": null,
          "target": "chapter_title",
          "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
          "paragraph_filter": { "where": [] },
          "transform": { "kind": "llm", "pattern": null, "replacement": null, "template": null, "instruction": "capitalise the title" } }
        """;

    private static string UpperCaseBatchReply(string prompt)
    {
        var entries = Regex.Matches(prompt, @"""index"":\s*(\d+),\s*""path"":\s*""[^""]*"",\s*""text"":\s*""([^""]*)""")
            .Select(m => $$"""{ "index": {{m.Groups[1].Value}}, "reasoning": "fake", "new_text": "{{m.Groups[2].Value.ToUpperInvariant()}}" }""");
        return "[" + string.Join(",", entries) + "]";
    }

    // Needs a real browser: the backdrop, Escape and the accessibility tree are the engine's.
    [Fact]
    public async Task Theme_editor_backdrop_click_asks_first_and_the_confirm_is_named()
    {
        await GotoAppAsync("settings/themes");
        await Page.Locator("[data-action='new-theme']").ClickAsync();
        var editor = Page.GetByRole(AriaRole.Dialog, new() { Name = "New theme" });
        await Expect(editor).ToBeVisibleAsync();
        await Expect(editor).ToHaveAccessibleNameAsync("New theme");

        await editor.Locator("input[name='name']").FillAsync("Sunset");
        // A click at the viewport corner lands on the ::backdrop, outside the dialog box.
        await Page.Mouse.ClickAsync(5, 5);

        var confirm = Page.GetByRole(AriaRole.Dialog, new() { Name = "Discard changes?" });
        await Expect(confirm).ToBeVisibleAsync();
        await Expect(confirm).ToHaveAccessibleNameAsync("Discard changes?");
        var snapshot = await confirm.AriaSnapshotAsync();
        Assert.Contains("dialog \"Discard changes?\"", snapshot);
        Console.WriteLine($"[proof] {Environment.GetEnvironmentVariable(PlaywrightFixture.BrowserVariable) ?? "chromium"} confirm aria snapshot:\n{snapshot}");

        await confirm.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(confirm).ToBeHiddenAsync();
        await Expect(editor).ToBeVisibleAsync();
        await Expect(editor.Locator("input[name='name']")).ToHaveValueAsync("Sunset");
    }

    // Needs a real browser: Escape on a real modal fires the cancel the guard runs on.
    [Fact]
    public async Task Edit_with_ai_escape_with_a_hand_edit_asks_first()
    {
        const string folder = "proof-edit-guard";
        await App.SeedMultiChapterProjectAsync(folder, "Edit Book", "Author", chapters: 3);
        App.FakeAi.LlmReply = p => p.Contains("structured edit plan") ? LlmPlan : UpperCaseBatchReply(p);
        try
        {
            await GotoAppAsync($"projects/{folder}/book");
            await Page.GetByTestId("book-actions").ClickAsync();
            await Page.Locator("[data-action='edit-with-ai']").First.ClickAsync();
            var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Edit with AI" });
            await Expect(dialog).ToBeVisibleAsync();

            await dialog.GetByTestId("instruction").FillAsync("capitalise chapter titles");
            await dialog.Locator("[data-action='analyze']").ClickAsync();
            await dialog.Locator("[data-action='generate']").ClickAsync();
            await dialog.Locator("[data-action='open-row']").First.ClickAsync(new() { Timeout = 30_000 });
            await dialog.GetByTestId("proposed").FillAsync("Chapter One, by hand");

            await Page.Keyboard.PressAsync("Escape");
            var confirm = Page.GetByRole(AriaRole.Dialog, new() { Name = "Close?" });
            await Expect(confirm).ToBeVisibleAsync();
            await Expect(confirm).ToContainTextAsync("Discard 1 hand-edited proposal?");

            await confirm.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(confirm).ToBeHiddenAsync();
            await Expect(dialog.GetByTestId("proposed")).ToHaveValueAsync("Chapter One, by hand");
        }
        finally
        {
            App.FakeAi.Reset();
        }
    }
}
