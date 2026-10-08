using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native app's shell at <c>/app2</c> (native-web 20), from the production bundle the in-proc
/// host serves: the app bar with breadcrumbs from the route chain, the nav rail with the context
/// and global groups, the theme loaded from the host on boot and switched from the quick menu, the
/// live hub connected with the open project's group joined, and a placeholder for every screen not
/// ported yet that links the same path in the Angular app. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class ShellTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Shell_frames_a_placeholder_page_that_names_the_screen_and_links_back_to_angular()
    {
        await GotoAppAsync("projects/foundation/cast");

        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(AppPath("projects/foundation/cast$")));

        await Expect(Page.Locator("app-root .shell__appbar .shell__brand")).ToHaveTextAsync(new Regex("Read2Me"));

        var placeholder = Page.Locator("r2m-placeholder-page");
        await Expect(placeholder.Locator(".r2m-page-header__title")).ToHaveTextAsync("Cast");
        await Expect(placeholder.Locator(".r2m-page-header__subtitle")).ToHaveTextAsync("Project: foundation");
        await Expect(placeholder.Locator(".r2m-empty-state__headline"))
            .ToHaveTextAsync("This screen has not moved to the native app yet");
        await Expect(placeholder.Locator("a.r2m-placeholder-page__link"))
            .ToHaveAttributeAsync("href", WebApp.Angular.Prefix() + "/projects/foundation/cast");

        // The project is not seeded, so the crumb keeps the folder name.
        await Expect(Page.Locator(".shell__crumb")).ToHaveTextAsync(["Projects", "foundation", "Cast"]);
        Assert.Equal("Read2Me · Projects · foundation · Cast", await Page.TitleAsync());
    }

    [Fact]
    public async Task Root_redirects_to_the_projects_shelf_and_unknown_paths_say_not_found()
    {
        await GotoAppAsync("");
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(AppPath("projects$")));
        await Expect(Page.Locator(".shell__crumb")).ToHaveTextAsync(["Projects"]);
        await Expect(Page.Locator(".shell__nav-group--context")).ToHaveCountAsync(0);

        await Page.GotoAsync(AppPath("no/such/screen"));
        await Expect(Page.Locator("r2m-placeholder-page .r2m-page-header__title")).ToHaveTextAsync("Not found");
        await Expect(Page.Locator("r2m-placeholder-page a.r2m-placeholder-page__link"))
            .ToHaveAttributeAsync("href", WebApp.Angular.Prefix() + "/no/such/screen");
    }

    [Fact]
    public async Task Nav_rail_and_breadcrumbs_follow_the_route_and_name_the_loaded_project()
    {
        await App.SeedProjectAsync("native-shell", "Native Shell Book", "A. Author");

        await GotoAppAsync("projects/native-shell/book");

        var contextLabels = Page.Locator(".shell__nav-group--context .shell__nav-label");
        await Expect(contextLabels).ToHaveTextAsync(["Overview", "Book", "Cast", "Export"]);
        await Expect(Page.Locator(".shell__nav-group--global .shell__nav-label")).ToHaveTextAsync(["Projects", "Settings"]);
        await Expect(Page.Locator(".shell__nav-item--active .shell__nav-label")).ToHaveTextAsync(["Book"]);

        // The project crumb shows the title the project shell loaded, and the tab title follows.
        await Expect(Page.Locator(".shell__crumb")).ToHaveTextAsync(["Projects", "Native Shell Book", "Book"]);
        await Expect(Page.Locator(".shell__crumb--current")).ToHaveTextAsync("Book");
        await Assertions.Expect(Page).ToHaveTitleAsync("Read2Me · Projects · Native Shell Book · Book");

        // Rail navigation is an in-app navigation: the URL, crumbs and placeholder all move.
        await Page.Locator(".shell__nav-group--context a", new() { HasText = "Cast" }).ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(AppPath("projects/native-shell/cast$")));
        await Expect(Page.Locator(".shell__crumb--current")).ToHaveTextAsync("Cast");
        await Expect(Page.Locator("r2m-placeholder-page .r2m-page-header__title")).ToHaveTextAsync("Cast");
        await Expect(Page.Locator(".shell__nav-item--active .shell__nav-label")).ToHaveTextAsync(["Cast"]);

        // A crumb link goes back up; the overview adds no crumb of its own.
        await Page.Locator("a.shell__crumb", new() { HasText = "Native Shell Book" }).ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(AppPath("projects/native-shell$")));
        await Expect(Page.Locator(".shell__crumb")).ToHaveTextAsync(["Projects", "Native Shell Book"]);
        await Expect(Page.Locator("r2m-placeholder-page .r2m-page-header__title")).ToHaveTextAsync("Overview");

        // Settings shows its own context group and keeps Settings active in the global group.
        await Page.Locator(".shell__settings").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(AppPath("settings/llm$")));
        await Expect(Page.Locator(".shell__crumb")).ToHaveTextAsync(["Settings", "LLM"]);
        // Leaving the project shell swaps the root outlet's element for the placeholder.
        await Expect(Page.Locator("main > r2m-outlet > r2m-placeholder-page .r2m-page-header__title")).ToHaveTextAsync("LLM");
        await Expect(Page.Locator(".shell__nav-group--context .shell__nav-label")).ToHaveCountAsync(9);
        await Expect(Page.Locator(".shell__nav-item--active .shell__nav-label")).ToHaveTextAsync(["LLM", "Settings"]);
    }

    [Fact]
    public async Task Rail_collapses_from_the_menu_button_and_stays_collapsed_across_a_reload()
    {
        await GotoAppAsync("projects");
        var rail = Page.Locator(".shell__rail");
        await Expect(rail).ToHaveClassAsync(new Regex("shell__rail--expanded"));
        await Expect(Page.Locator(".shell__nav-label").First).ToBeVisibleAsync();

        await Page.Locator(".shell__menu").ClickAsync();
        await Expect(rail).Not.ToHaveClassAsync(new Regex("shell__rail--expanded"));
        await Expect(Page.Locator(".shell__nav-label").First).ToBeHiddenAsync();
        await Expect(Page.Locator(".shell__nav-group--global a").First).ToHaveAttributeAsync("data-tooltip", "Projects");

        await Page.ReloadAsync();
        await Expect(Page.Locator(".shell__rail")).Not.ToHaveClassAsync(new Regex("shell__rail--expanded"));
    }

    [Fact]
    public async Task Live_hub_connects_and_opening_a_project_joins_its_group()
    {
        await App.SeedProjectAsync("native-live", "Native Live Book", "A. Author");

        // Every frame the page sends over the hub socket: the JoinProject invocation proves the
        // project shell joined the group (receipts only reach group members).
        var sent = new List<string>();
        var joined = new TaskCompletionSource();
        Page.WebSocket += (_, ws) => ws.FrameSent += (_, frame) =>
        {
            var text = frame.Text ?? "";
            lock (sent) sent.Add(text);
            if (text.Contains("\"target\":\"JoinProject\"") && text.Contains("\"native-live\"")) joined.TrySetResult();
        };

        await GotoAppAsync("projects/native-live/book");

        var conn = Page.Locator(".shell__conn");
        await Expect(conn).ToHaveClassAsync(new Regex("shell__conn--connected"));
        await Expect(conn).ToHaveAttributeAsync("aria-label", "Live updates connected");

        var timeout = Task.Delay(5_000);
        if (await Task.WhenAny(joined.Task, timeout) == timeout)
        {
            lock (sent) Assert.Fail($"No JoinProject for native-live among the hub frames sent: {string.Join(" | ", sent)}");
        }
    }

    [Fact]
    public async Task Theme_loads_from_the_host_on_boot_and_the_quick_menu_switches_and_persists_it()
    {
        try
        {
            await GotoAppAsync("projects");
            var html = Page.Locator("html");
            await Expect(html).ToHaveAttributeAsync("data-theme", "light");
            await Expect(Page.Locator("#r2m-theme")).ToHaveCountAsync(1);

            await Page.Locator(".shell__theme").ClickAsync();
            var menu = Page.Locator("#r2m-theme-menu");
            await Expect(menu).ToBeVisibleAsync();
            await menu.Locator("[data-scheme='dark']").ClickAsync();

            await Expect(html).ToHaveAttributeAsync("data-theme", "dark");
            await Expect(menu).ToBeHiddenAsync();

            // The host stores the selection, so a reload (and every other client) gets Dark.
            await Page.ReloadAsync();
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

            var selection = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/settings/themes/selection");
            Assert.Contains($"\"selectedThemeId\":{await ThemeIdAsync("Dark")}", await selection.TextAsync());
        }
        finally
        {
            // The selection is host state shared by every test in the collection: put Light back.
            await Page.APIRequest.PutAsync($"{App.BaseUrl}/api/settings/themes/selection", new()
            {
                DataObject = new { selectedThemeId = await ThemeIdAsync("Light"), followSystemPreference = false },
            });
        }
    }

    private async Task<int> ThemeIdAsync(string name)
    {
        var themes = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/settings/themes");
        using var doc = JsonDocument.Parse(await themes.TextAsync());
        return doc.RootElement.EnumerateArray().First(t => t.GetProperty("name").GetString() == name).GetProperty("id").GetInt32();
    }
}
