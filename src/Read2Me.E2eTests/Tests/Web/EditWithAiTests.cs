using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Edit with AI in the web reader (Angular ticket 19) end to end on the fake-AI host: instruct →
/// plan → propose → review, with a hand edit and a per-row retry, then one apply that the reader
/// picks up from its own receipt and Blazor agrees with.
/// </summary>
[Collection(E2eCollection.Name)]
public class EditWithAiTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    /// <summary>An LLM plan that hands every chapter title to the model to rewrite.</summary>
    private const string LlmPlan =
        """
        { "reasoning": "capitalise every chapter title", "supported": true, "unsupported_reason": null,
          "target": "chapter_title",
          "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
          "paragraph_filter": { "where": [] },
          "transform": { "kind": "llm", "pattern": null, "replacement": null, "template": null, "instruction": "capitalise the title" } }
        """;

    private static bool IsPlanPrompt(string prompt) => prompt.Contains("structured edit plan");

    /// <summary>Answers a batch prompt with every listed text upper-cased, so rows are checkable.</summary>
    private static string UpperCaseBatchReply(string prompt)
    {
        var entries = Regex
            .Matches(prompt, @"""index"":\s*(\d+),\s*""path"":\s*""[^""]*"",\s*""text"":\s*""([^""]*)""")
            .Select(m => $$"""{ "index": {{m.Groups[1].Value}}, "reasoning": "fake", "new_text": "{{m.Groups[2].Value.ToUpperInvariant()}}" }""");
        return "[" + string.Join(",", entries) + "]";
    }

    private async Task OpenDialogAsync(string folder, string instruction)
    {
        await GotoAppAsync($"/app/projects/{folder}/book");
        await Page.Locator("[data-testid='book-actions']").ClickAsync();
        await Page.Locator(".mat-mdc-menu-panel [data-action='edit-with-ai']").ClickAsync();

        var dialog = Page.Locator("app-edit-with-ai-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("[data-testid='instruction']").FillAsync(instruction);
        await dialog.Locator("[data-action='analyze']").ClickAsync();
    }

    [Fact]
    public async Task Instruct_plan_propose_hand_edit_retry_and_apply()
    {
        const string folder = "web-edit-ai";
        await App.SeedMultiChapterProjectAsync(folder, "Edit With AI Book", "A. Author", chapters: 3);
        App.FakeAi.LlmReply = p => IsPlanPrompt(p) ? LlmPlan : UpperCaseBatchReply(p);

        await OpenDialogAsync(folder, "capitalise chapter titles");
        var dialog = Page.Locator("app-edit-with-ai-dialog");

        // 1. Plan: what the host understood, and how big the job is.
        await Expect(dialog.Locator(".edit")).ToHaveAttributeAsync("data-phase", "plan");
        await Expect(dialog.Locator("[data-testid='plan-counts']")).ToContainTextAsync("3 matching items");

        // 2. Propose: one row per chapter, every appliable one ticked.
        await dialog.Locator("[data-action='generate']").ClickAsync();
        await Expect(dialog.Locator(".edit")).ToHaveAttributeAsync("data-phase", "review",
            new() { Timeout = 30_000 });
        await Expect(dialog.Locator("[data-row]")).ToHaveCountAsync(3);
        await Expect(dialog.Locator("[data-testid='selected-count']")).ToHaveTextAsync("3 of 3 selected");
        await Expect(dialog.Locator("[data-row='0']")).ToContainTextAsync("CHAPTER 1");

        // 3. Correct the AI by hand on the first row.
        var first = dialog.Locator("[data-row='0']");
        await first.Locator("[data-action='open-row']").ClickAsync();
        await first.Locator("[data-testid='proposed']").FillAsync("Chapter The First");
        await Expect(first.Locator("r2m-status-chip")).ToContainTextAsync("Edited");
        await first.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        // 4. Ask the AI again for the second row, steered by a hint.
        App.FakeAi.LlmReply = _ => """[ { "index": 0, "reasoning": "fake", "new_text": "Chapter The Second" } ]""";
        var second = dialog.Locator("[data-row='1']");
        await second.Locator("[data-action='open-row']").ClickAsync();
        await second.Locator("[data-testid='hint']").FillAsync("spell the number out");
        await second.Locator("[data-action='retry-row']").ClickAsync();
        await Expect(second).ToContainTextAsync("Chapter The Second", new() { Timeout = 30_000 });
        string lastPrompt;
        lock (App.FakeAi.LlmPromptsSeen) lastPrompt = App.FakeAi.LlmPromptsSeen[^1];
        Assert.Contains("spell the number out", lastPrompt);
        await second.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        // 5. Drop the third row, then apply the two that are left as one command.
        await dialog.Locator("[data-row='2'] input[type=checkbox]").ClickAsync();
        await Expect(dialog.Locator("[data-testid='selected-count']")).ToHaveTextAsync("2 of 3 selected");
        await dialog.Locator("[data-action='apply']").ClickAsync();

        // 6. The dialog closes and the reader is already showing the result (its own receipt).
        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = 30_000 });
        await Expect(Page.Locator(".tree__title"))
            .ToHaveTextAsync(["Chapter The First", "Chapter The Second", "Chapter 3"]);

        // 7. The host agrees, and so does Blazor.
        var children = await Page.APIRequest.GetAsync(
            $"{App.BaseUrl}/api/projects/{folder}/book");
        Assert.Contains("\"totalChapters\":3", await children.TextAsync());
        await GotoAsync($"/project/{folder}");
        await Expect(Page.GetByText("Chapter The First").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.GetByText("Chapter The Second").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Cancelling_mid_proposal_keeps_the_rows_computed_so_far()
    {
        const string folder = "web-edit-ai-cancel";
        await App.SeedMultiChapterProjectAsync(folder, "Cancel Book", "A. Author", chapters: 24);
        App.FakeAi.LlmReply = p => IsPlanPrompt(p) ? LlmPlan : UpperCaseBatchReply(p);

        await OpenDialogAsync(folder, "capitalise chapter titles");
        var dialog = Page.Locator("app-edit-with-ai-dialog");
        await Expect(dialog.Locator(".edit")).ToHaveAttributeAsync("data-phase", "plan");

        // Slow the batches down so the run is still going when Cancel is clicked.
        App.FakeAi.LlmDelay = TimeSpan.FromMilliseconds(600);
        await dialog.Locator("[data-action='generate']").ClickAsync();
        await Expect(dialog.Locator("[data-testid='progress']")).ToContainTextAsync("8 of 24",
            new() { Timeout = 30_000 });
        await dialog.Locator("[data-action='cancel-proposing']").ClickAsync();

        // The partial rows stay reviewable, and Apply is live for them.
        await Expect(dialog.Locator(".edit")).ToHaveAttributeAsync("data-phase", "review",
            new() { Timeout = 30_000 });
        var rows = await dialog.Locator("[data-row]").CountAsync();
        Assert.InRange(rows, 8, 16);
        await Expect(dialog.Locator("[data-action='apply']")).ToBeEnabledAsync();
        await Expect(dialog.Locator("[data-action='apply']")).ToContainTextAsync($"Apply {rows} selected");
    }
}
