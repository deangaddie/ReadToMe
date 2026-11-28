using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #486: the settings form labels its controls and shows errors on blur. Deleted in the PR's final commit.</summary>
public class SettingsFormLabelsProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Labels_focus_and_toggle_their_controls_and_a_number_error_waits_for_blur()
    {
        var name = $"proof-{Guid.NewGuid():N}";
        var create = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/settings/voice-design", new
        {
            name,
            type = "VoxCpm2",
            settingsJson = """{"baseUrl":"http://example-voxcpm"}""",
        });
        create.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        try
        {
            await Page.AddInitScriptAsync("""
                window.__said = [];
                const spy = function (text, options) { window.__said.push({ text, priority: options?.priority }); };
                Element.prototype.ariaNotify = spy;
                Document.prototype.ariaNotify = spy;
                """);
            await GotoAppAsync("settings/voice-design");
            await Page.Locator(".r2m-config-list__main", new() { HasText = name }).ClickAsync();
            var form = Page.Locator("r2m-settings-form");
            await Expect(form).ToBeVisibleAsync();

            // Each control is named by its visible label; the range by "… slider".
            var retry = form.GetByRole(AriaRole.Switch, new() { Name = "Retry Bad Cases", Exact = true });
            await Expect(retry).ToBeVisibleAsync();
            var steps = form.GetByRole(AriaRole.Spinbutton, new() { Name = "LocDiT Steps", Exact = true });
            await Expect(steps).ToBeVisibleAsync();
            await Expect(form.GetByRole(AriaRole.Slider, new() { Name = "LocDiT Steps slider", Exact = true })).ToBeVisibleAsync();
            await Expect(form.GetByLabel("Model", new() { Exact = true })).ToHaveAttributeAsync("spellcheck", "false");

            // Clicking a boolean field's name toggles its switch.
            var wasChecked = await retry.IsCheckedAsync();
            await form.Locator("label.r2m-settings-form__label", new() { HasText = "Retry Bad Cases" }).ClickAsync();
            if (wasChecked) await Expect(retry).Not.ToBeCheckedAsync();
            else await Expect(retry).ToBeCheckedAsync();

            // Clicking a number field's name focuses its number box.
            await form.Locator("label.r2m-settings-form__label", new() { HasText = "LocDiT Steps" }).ClickAsync();
            await Expect(steps).ToBeFocusedAsync();

            // Typing "-" shows no error; Tab shows it, linked and announced once.
            await steps.FillAsync("");
            await steps.PressSequentiallyAsync("-");
            var row = form.Locator("[data-key='inference_timesteps']");
            var error = row.Locator(".r2m-settings-form__error");
            await Page.WaitForTimeoutAsync(800);
            await Expect(error).ToHaveCountAsync(0);
            await Expect(steps).ToHaveAttributeAsync("aria-invalid", "false");
            Assert.Equal("[]", await Page.EvaluateAsync<string>("JSON.stringify(window.__said)"));

            await Page.Keyboard.PressAsync("Tab");
            await Expect(error).ToBeVisibleAsync();
            Assert.Null(await error.GetAttributeAsync("role"));
            await Expect(steps).ToHaveAttributeAsync("aria-invalid", "true");
            await Expect(steps).ToHaveAttributeAsync("aria-describedby", (await error.GetAttributeAsync("id"))!);
            await Expect(steps).ToHaveAccessibleDescriptionAsync("Must be a number ≥ 1 ≤ 50.");
            await Page.WaitForFunctionAsync("window.__said.length >= 1");
            await Page.WaitForTimeoutAsync(1000);
            Assert.Equal("""[{"text":"LocDiT Steps: Must be a number ≥ 1 ≤ 50.","priority":"normal"}]""",
                await Page.EvaluateAsync<string>("JSON.stringify(window.__said)"));
        }
        finally
        {
            await Http.DeleteAsync($"{App.BaseUrl}/api/settings/voice-design/{id}");
        }
    }
}
