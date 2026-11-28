using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #503: the nav rail, an Expansion panel chevron and a switch knob transition
/// only under <c>prefers-reduced-motion: no-preference</c>. Colour and shadow transitions stay.
/// </summary>
public class ReducedMotionProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // "property duration" of the element, or of its pseudo-element.
    private const string Transition = """
        (el, pseudo) => {
          const s = getComputedStyle(el, pseudo);
          return `${s.transitionProperty} ${s.transitionDuration}`;
        }
        """;

    // Clicks `trigger`, then at once reads the transitions running on the element (or its
    // pseudo-element) and one computed value of its new state.
    private const string ClickAndRead = """
        async (el, [trigger, pseudo, property]) => {
          document.querySelector(trigger).click();
          await new Promise((done) => setTimeout(done));
          const value = getComputedStyle(el, pseudo).getPropertyValue(property);
          const running = document
            .getAnimations()
            .filter((a) => a.effect.target === el && (a.effect.pseudoElement ?? null) === pseudo)
            .map((a) => a.transitionProperty);
          return `${running.sort().join(',')}|${value}`;
        }
        """;

    // Needs a real browser: the transitions are computed style under an emulated media feature.
    [Theory]
    [InlineData(ReducedMotion.Reduce)]
    [InlineData(ReducedMotion.NoPreference)]
    public async Task Spatial_transitions_follow_the_motion_preference(ReducedMotion motion)
    {
        var moves = motion == ReducedMotion.NoPreference;
        await Page.EmulateMediaAsync(new() { ReducedMotion = motion });

        // Switch knob, with the track's colour transition and a theme card's shadow beside it.
        await GotoAppAsync("settings/themes");
        await Check("theme card shadow", Page.Locator(".themes__card").First, null, "box-shadow 0.12s");
        var input = Page.Locator(".r2m-switch input[name=followSystem]");
        await Expect(input).ToBeEnabledAsync();
        await Check("switch knob", input, "::before", moves ? "translate 0.12s" : "all 0s");
        await Check("switch track", input, null, "background 0.12s");
        var wasChecked = await input.IsCheckedAsync();
        var knob = await Act(input, ".r2m-switch input[name=followSystem]", "::before", "translate");
        Assert.Equal($"{(moves ? "translate" : "")}", knob.Running);
        // Under reduce the knob is at its end state at once.
        if (!moves) Assert.Equal(wasChecked ? "none" : "16px", knob.Value);

        // Nav rail.
        var rail = Page.Locator(".shell__rail");
        await Check("rail", rail, null, moves ? "width 0.15s" : "all 0s");
        var before = (await rail.BoundingBoxAsync())!.Width;
        var width = await Act(rail, ".shell__menu", null, "width");
        Assert.Equal(moves ? "width" : "", width.Running);
        // A width transition object exists only under no-preference. The docked rail is sized by
        // flex-basis, which is not transitioned, so it is at its end width at once under both settings.
        await Page.WaitForTimeoutAsync(500);
        var after = (await rail.BoundingBoxAsync())!.Width;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{Page.Context.Browser!.BrowserType.Name} {motion}: rail width {before}px before, {width.Value} just after the click, {after}px settled");
        Assert.NotEqual(before, after);
        Assert.Equal($"{after - 1}px", width.Value); // the box adds the 1 px border

        // The rail frame by frame, expanding and collapsing (wide layout, where flex-basis sizes it).
        await Page.Locator(".shell__menu").ClickAsync();
        await Page.WaitForTimeoutAsync(500);
        var samples = await Page.EvaluateAsync<string>(
            """
            async () => {
              const rail = document.querySelector('.shell__rail');
              const main = document.querySelector('.shell__main');
              const out = [];
              for (const _ of [1, 2]) {
                document.querySelector('.shell__menu').click();
                for (let i = 0; i < 6; i++) {
                  await new Promise((done) => requestAnimationFrame(() => setTimeout(done, 15)));
                  out.push(`${rail.getBoundingClientRect().width}/${main.getBoundingClientRect().width}`);
                }
                await new Promise((done) => setTimeout(done, 400));
                out.push('|');
              }
              return out.join(' ');
            }
            """);
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{Page.Context.Browser!.BrowserType.Name} {motion}: rail/main widths per frame: {samples}");

        // Expansion panel chevron.
        await GotoAppAsync("settings/prompts");
        var summary = Page.Locator(".prompt-editor__preview > summary");
        await Expect(summary).ToBeVisibleAsync();
        await Check("chevron", summary, "::after", moves ? "rotate 0.12s" : "all 0s");
        var rotate = await Act(summary, ".prompt-editor__preview > summary", "::after", "rotate");
        Assert.Equal(moves ? "rotate" : "", rotate.Running);
        if (!moves) Assert.Equal("180deg", rotate.Value);

        async Task Check(string name, ILocator target, string? pseudo, string expected)
        {
            var actual = await target.EvaluateAsync<string>(Transition, pseudo);
            TestContext.Current.TestOutputHelper!.WriteLine($"{Page.Context.Browser!.BrowserType.Name} {motion}: {name} = {actual}");
            Assert.Equal(expected, actual);
        }

        async Task<(string Running, string Value)> Act(ILocator target, string trigger, string? pseudo, string property)
        {
            var read = await target.EvaluateAsync<string>(ClickAndRead, new object?[] { trigger, pseudo, property });
            TestContext.Current.TestOutputHelper!.WriteLine($"{Page.Context.Browser!.BrowserType.Name} {motion}: {trigger} running|{property} = {read}");
            var parts = read.Split('|');
            return (parts[0], parts[1]);
        }
    }
}
