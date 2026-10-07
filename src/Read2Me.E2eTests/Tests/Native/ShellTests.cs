using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native app's shell loads at <c>/app2</c> from the production bundle the in-proc host serves
/// (native-web 17): the app bar frames a placeholder page, which links the same path in the Angular
/// app. Runs in Chromium and Firefox; no screen is ported yet, so this is the whole native surface.
/// </summary>
[Collection(E2eCollection.Name)]
public class ShellTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Shell_frames_a_placeholder_page_that_links_back_to_angular()
    {
        await GotoAppAsync("projects/foundation/book");

        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(AppPath("projects/foundation/book$")));

        await Expect(Page.Locator("app-root .shell__appbar .shell__brand")).ToHaveTextAsync(new Regex("Read2Me"));

        var placeholder = Page.Locator("r2m-placeholder-page");
        await Expect(placeholder.Locator(".r2m-empty-state__headline"))
            .ToHaveTextAsync("This screen has not moved to the native app yet");
        await Expect(placeholder.Locator("a.r2m-placeholder-page__link"))
            .ToHaveAttributeAsync("href", WebApp.Angular.Prefix() + "/projects/foundation/book");

        Assert.Equal("Read2Me", await Page.TitleAsync());
    }
}
