using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Setup check's start banner and app-bar shield in a real browser: layout only. The banner
/// spans the page between the app bar and the body, and the shield carries the failed count.
/// When each shows, dismissal and the first-run landing are spec-tested (surfacing.spec.ts,
/// shell.spec.ts).
/// </summary>
public class SetupCheckSurfacingTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task A_failed_check_puts_the_banner_under_the_app_bar_and_the_count_on_the_shield()
    {
        App.Ffmpeg.Missing = true;
        var failed = await FailedAfterQuickCheckAsync();
        Assert.True(failed > 0);

        await GotoAppAsync("projects");

        var bar = Page.Locator(".shell__appbar");
        var banner = Page.Locator(".shell__setup-banner");
        var body = Page.Locator(".shell__body");
        await Expect(banner).ToBeVisibleAsync();
        await Expect(banner).ToContainTextAsync($"Setup check: {failed} failed.");
        var barBox = (await bar.BoundingBoxAsync())!;
        var bannerBox = (await banner.BoundingBoxAsync())!;
        var bodyBox = (await body.BoundingBoxAsync())!;
        Assert.True(bannerBox.Y >= barBox.Y + barBox.Height - 1,
            $"banner (y={bannerBox.Y}) should sit under the app bar (bottom={barBox.Y + barBox.Height})");
        Assert.True(bannerBox.Y + bannerBox.Height <= bodyBox.Y + 1,
            $"banner (bottom={bannerBox.Y + bannerBox.Height}) should sit above the body (y={bodyBox.Y})");
        Assert.True(Math.Abs(bannerBox.Width - barBox.Width) <= 1, "banner should span the app bar's width");

        var count = Page.Locator(".shell__appbar .shell__setup-shield-count");
        await Expect(count).ToBeVisibleAsync();
        await Expect(count).ToHaveTextAsync(failed.ToString());
        var shieldBox = (await Page.Locator(".shell__setup-shield").BoundingBoxAsync())!;
        var countBox = (await count.BoundingBoxAsync())!;
        Assert.True(countBox.X >= shieldBox.X - 1 && countBox.X + countBox.Width <= shieldBox.X + shieldBox.Width + 1,
            "the count badge should sit on the shield");
    }

    [Fact]
    public async Task The_shield_follows_a_Full_check_over_the_real_hub()
    {
        await FailedAfterQuickCheckAsync();
        await GotoAppAsync("projects");
        var shield = Page.Locator(".shell__appbar .shell__setup-shield");
        await Expect(shield).ToBeVisibleAsync();

        // Started from the agent API, as another tab would: only the hub tells this page.
        App.FfmpegEncode.HoldNext();
        try
        {
            var started = await Http.PostAsync($"{App.BaseUrl}/api/setup-check/full", null);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            await App.FfmpegEncode.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await Expect(shield).ToHaveClassAsync(new Regex("shell__setup-shield--busy"));
            await Expect(shield).ToHaveAttributeAsync("aria-label", "Full check 1/3");
            await Expect(shield.Locator(".r2m-spinner")).ToBeVisibleAsync();
        }
        finally
        {
            App.FfmpegEncode.Release();
        }

        await Expect(shield.Locator(".r2m-spinner")).ToHaveCountAsync(0);
        await Expect(shield).Not.ToHaveClassAsync(new Regex("shell__setup-shield--busy"));
    }

    /// <summary>Runs the Quick check and counts what failed: docker may fail too on a machine without it.</summary>
    private async Task<int> FailedAfterQuickCheckAsync()
    {
        var response = await Http.PostAsync($"{App.BaseUrl}/api/setup-check/quick", null);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .Count(r => r.GetProperty("result").GetString() == "failed");
    }
}
