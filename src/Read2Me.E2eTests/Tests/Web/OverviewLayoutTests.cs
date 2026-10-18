using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Overview under its one breakpoint (guidance-primitives spec §3, 1100 px): the stepper comes
/// before the details card, a row's head splits so the `?` stays on the title line with the
/// checkpoint pills on a second line, and the `?` opens the step's help inline in the row (after
/// the head row, before the detail line) rather than in the hidden right column. Layout only:
/// the toggle, the pills and the page state are spec-tested.
/// </summary>
public class OverviewLayoutTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task At_phone_width_the_stepper_leads_and_help_opens_inline_under_a_split_head_row()
    {
        await App.SeedProjectAsync("overview-layout", "Overview Layout", "A. Author");
        await Page.SetViewportSizeAsync(390, 800);

        await GotoAppAsync("projects/overview-layout");
        var stepper = Page.Locator(".r2m-pipeline");
        var details = Page.Locator("r2m-project-details-panel");
        await Expect(stepper).ToBeVisibleAsync();
        await Expect(details).ToBeVisibleAsync();
        var stepperBox = (await stepper.BoundingBoxAsync())!;
        var detailsBox = (await details.BoundingBoxAsync())!;
        Assert.True(stepperBox.Y + stepperBox.Height <= detailsBox.Y + 1,
            $"details card (y={detailsBox.Y}) should follow the stepper (bottom={stepperBox.Y + stepperBox.Height})");

        var row = Page.Locator(".r2m-pipeline__step[data-step='structure']");
        var title = row.Locator(".r2m-pipeline__title");
        var help = row.Locator(".r2m-pipeline__help-toggle");
        var pills = row.Locator(".r2m-pipeline__checkpoint");
        await Expect(pills.GetByRole(AriaRole.Button, new() { Name = "Confirm" })).ToBeVisibleAsync();
        var titleBox = (await title.BoundingBoxAsync())!;
        var helpBox = (await help.BoundingBoxAsync())!;
        var pillsBox = (await pills.BoundingBoxAsync())!;
        // `?` on line one: it overlaps the title vertically and sits at the row's right edge.
        Assert.True(helpBox.Y < titleBox.Y + titleBox.Height && helpBox.Y + helpBox.Height > titleBox.Y,
            $"? (y={helpBox.Y}) should share the title line (y={titleBox.Y})");
        Assert.True(helpBox.X > titleBox.X + titleBox.Width, "? should be right of the title");
        // Pills on line two: below the title line.
        Assert.True(pillsBox.Y >= titleBox.Y + titleBox.Height - 1,
            $"pills (y={pillsBox.Y}) should sit under the title line (bottom={titleBox.Y + titleBox.Height})");

        await help.ClickAsync();
        var inline = row.Locator(".r2m-pipeline__help-inline .overview__help--inline");
        await Expect(inline).ToBeVisibleAsync();
        await Expect(Page.Locator(".overview__help--side")).ToBeHiddenAsync();
        var head = row.Locator(".r2m-pipeline__head");
        var detail = row.Locator(".r2m-pipeline__detail");
        var headBox = (await head.BoundingBoxAsync())!;
        var inlineBox = (await inline.BoundingBoxAsync())!;
        var detailBox = (await detail.BoundingBoxAsync())!;
        Assert.True(inlineBox.Y >= headBox.Y + headBox.Height - 1, "inline help should follow the head row");
        Assert.True(inlineBox.Y + inlineBox.Height <= detailBox.Y + 1, "inline help should precede the detail line");
        // The details card stays on the page below the stepper while help is open at this width.
        await Expect(details).ToBeVisibleAsync();
    }
}
