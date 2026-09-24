using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// <c>/settings/llm</c> (Angular ticket 21) against the real host: config create / edit / make
/// default / delete with Get models, the test console streaming and stopping, and the attribution
/// chain reordered and read back over the API.
/// </summary>
[Collection(E2eCollection.Name)]
public class LlmSettingsTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private ILocator Row(string name) => Page.Locator(".r2m-config-list__row", new()
    {
        Has = Page.Locator(".r2m-config-list__name", new() { HasTextRegex = new Regex($"^{Regex.Escape(name)}$") }),
    });
    private ILocator Field(string field) => Page.Locator($"app-llm-config-editor [data-field='{field}']");
    private ILocator Save => Page.Locator("r2m-config-editor-frame button", new() { HasText = "Save" });

    private async Task RowActionAsync(string name, string action)
    {
        await Page.Locator($"[aria-label='Actions for {name}']").ClickAsync();
        await Page.Locator(".mat-mdc-menu-item", new() { HasText = action }).ClickAsync();
    }

    private async Task<JsonElement> GetJsonAsync(string path) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}{path}")).RootElement;

    [Fact]
    public async Task Config_is_created_with_a_fetched_model_edited_made_default_and_deleted()
    {
        var name = $"web-llm-{Guid.NewGuid():N}"[..16];
        try
        {
            await GotoAppAsync("/app/settings/llm");
            await Expect(Row("fake")).ToContainTextAsync("Default");

            await Page.Locator("r2m-config-list button", new() { HasText = "New" }).ClickAsync();
            await Expect(Page.Locator(".r2m-config-editor-frame__title")).ToHaveTextAsync("New configuration");
            await Field("name").FillAsync(name);

            // Validation speaks before anything is sent.
            await Field("baseUrl").FillAsync("nowhere");
            await Save.ClickAsync();
            await Expect(Page.Locator(".llm-editor__error")).ToContainTextAsync("Base URL must be a valid absolute URL");

            // A server that cannot be asked leaves free text with the hint…
            await Field("baseUrl").FillAsync("http://no-such-llm");
            await Page.Locator("[data-action='get-models']").ClickAsync();
            await Expect(Page.Locator("[data-role='models-hint']")).ToContainTextAsync("Could not fetch models");

            // …and one that answers fills the select.
            await Field("baseUrl").FillAsync("http://fake-llm");
            await Page.Locator("[data-action='get-models']").ClickAsync();
            await Page.Locator("mat-select[data-field='model']").ClickAsync();
            await Page.Locator("mat-option", new() { HasText = FakeAiRoutingHandler.DefaultModel }).ClickAsync();
            await Save.ClickAsync();

            await Expect(Row(name)).ToContainTextAsync(FakeAiRoutingHandler.DefaultModel);
            await Expect(Page.Locator(".r2m-config-editor-frame__title")).ToHaveTextAsync(name);

            // Edit in place.
            await Field("attributionBatchSize").FillAsync("4");
            await Expect(Page.Locator(".r2m-config-editor-frame__dirty")).ToBeVisibleAsync();
            await Save.ClickAsync();
            await Expect(Page.Locator(".r2m-config-editor-frame__dirty")).ToHaveCountAsync(0);
            var stored = (await GetJsonAsync("/api/settings/llm")).EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == name);
            Assert.Equal(4, stored.GetProperty("attributionBatchSize").GetInt32());

            await RowActionAsync(name, "Make default");
            await Expect(Row(name)).ToContainTextAsync("Default");
            await Expect(Row("fake")).Not.ToContainTextAsync("Default");
            Assert.Equal(name, (await GetJsonAsync("/api/settings/llm/active")).GetProperty("name").GetString());

            await RowActionAsync("fake", "Make default");
            await Expect(Row("fake")).ToContainTextAsync("Default");

            await RowActionAsync(name, "Delete");
            await Page.Locator("r2m-confirm-dialog .r2m-confirm-dialog__confirm").ClickAsync();
            await Expect(Row(name)).ToHaveCountAsync(0);
        }
        finally
        {
            await RestoreAsync(name);
        }
    }

    [Fact]
    public async Task Test_console_streams_the_reply_inline_and_Stop_aborts_a_slow_one()
    {
        App.FakeAi.LlmReply = _ => "The quick brown fox.";
        try
        {
            await GotoAppAsync("/app/settings/llm");
            var console = Page.Locator("app-llm-test-console");
            await Expect(console).ToContainTextAsync("Test \"fake\"");

            await console.Locator("[data-field='prompt']").FillAsync("Say something");
            await console.Locator("[data-action='send']").ClickAsync();
            await Expect(console.Locator("r2m-stream-llm")).ToContainTextAsync("The quick brown fox.", new() { Timeout = 10_000 });
            await Expect(console.Locator("[data-action='stop']")).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            await Expect(console.Locator(".r2m-throughput__table")).ToContainTextAsync("fake", new() { Timeout = 10_000 });

            App.FakeAi.LlmDelay = TimeSpan.FromSeconds(30);
            await console.Locator("[data-field='prompt']").FillAsync("Take your time");
            await console.Locator("[data-action='send']").ClickAsync();
            await console.Locator("[data-action='stop']").ClickAsync();
            await Expect(console.Locator("[data-action='stop']")).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            await Expect(console.Locator("[data-action='send']")).ToBeEnabledAsync();
            Assert.False((await GetJsonAsync("/api/settings/llm/test")).GetProperty("running").GetBoolean());
        }
        finally
        {
            App.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Chain_steps_are_added_reordered_and_self_consistency_toggled()
    {
        try
        {
            await GotoAppAsync("/app/settings/llm");
            var chain = Page.Locator("app-llm-chain-card");
            await Expect(chain.Locator(".chain__alert--info")).ToContainTextAsync("fake");

            await chain.Locator("[data-action='add-step']").ClickAsync();
            await Page.Locator(".mat-mdc-menu-item", new() { HasText = "fake (simple)" }).First.ClickAsync();
            await Expect(chain.Locator(".chain__step")).ToHaveCountAsync(1);
            await chain.Locator("[data-action='add-step']").ClickAsync();
            await Page.GetByRole(AriaRole.Menuitem, new() { Name = "fake (thinking)", Exact = true }).ClickAsync();
            await Expect(chain.Locator(".chain__step")).ToHaveCountAsync(2);

            await chain.Locator("[aria-label='Move fake down']").First.ClickAsync();
            await Expect(chain.Locator(".chain__step").First).ToContainTextAsync("Thinking");
            await chain.Locator("mat-slide-toggle button").ClickAsync();

            await Expect(chain.Locator("mat-slide-toggle button")).ToHaveAttributeAsync("aria-checked", "true");
            // The switch is held disabled until its PUT has answered.
            await Expect(chain.Locator("mat-slide-toggle button")).ToBeEnabledAsync();
            var stored = await GetJsonAsync("/api/settings/llm/attribution-chain");
            Assert.True(stored.GetProperty("selfConsistency").GetBoolean());
            var steps = stored.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(2, steps.Count);
            Assert.True(steps[0].GetProperty("thinking").GetBoolean());
            Assert.Equal(1, steps[1].GetProperty("promptStyle").GetInt32());

            await chain.Locator("[aria-label='Remove fake']").First.ClickAsync();
            await Expect(chain.Locator(".chain__step")).ToHaveCountAsync(1);
        }
        finally
        {
            await Http.PutAsJsonAsync($"{App.BaseUrl}/api/settings/llm/attribution-chain",
                new { steps = Array.Empty<object>(), selfConsistency = false });
        }
    }

    /// <summary>Leaves the shared host as the next test expects it: "fake" default, no stray config.</summary>
    private async Task RestoreAsync(string name)
    {
        var configs = (await GetJsonAsync("/api/settings/llm")).EnumerateArray().ToList();
        var fake = configs.First(c => c.GetProperty("name").GetString() == "fake").GetProperty("id").GetInt32();
        await Http.PutAsJsonAsync($"{App.BaseUrl}/api/settings/llm/active", new { id = fake });
        foreach (var stray in configs.Where(c => c.GetProperty("name").GetString() == name))
            await Http.DeleteAsync($"{App.BaseUrl}/api/settings/llm/{stray.GetProperty("id").GetInt32()}");
    }
}
