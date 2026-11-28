using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #488: a disabled or blocked switch looks disabled. Deleted in the PR's final commit.</summary>
public class SwitchDisabledProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Look = """
        el => {
          const s = getComputedStyle(el);
          const l = getComputedStyle(el.closest('label'));
          return { Opacity: s.opacity, Cursor: s.cursor, Label: l.color, Disabled: el.disabled, Aria: el.getAttribute('aria-disabled') };
        }
        """;

    private record Style
    {
        public string Opacity { get; init; } = "";
        public string Cursor { get; init; } = "";
        public string Label { get; init; } = "";
        public bool Disabled { get; init; }
        public string? Aria { get; init; }
    }

    [Fact]
    public async Task A_switch_blocked_by_viewing_a_History_step_looks_disabled()
    {
        await App.SeedProjectAsync("switch-proof", "Switch Proof", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/switch-proof");
        var narratorOnly = Page.GetByRole(AriaRole.Switch, new() { Name = "Narrator only" });
        await Expect(narratorOnly).ToBeVisibleAsync();

        var live = await narratorOnly.EvaluateAsync<Style>(Look);
        Console.WriteLine($"live: {live}");
        Assert.Equal("1", live.Opacity);
        Assert.Equal("pointer", live.Cursor);

        await Page.EvaluateAsync("""
            async () => {
              await fetch('/api/projects/switch-proof/history');
              await fetch('/api/projects/switch-proof', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title: 'Renamed' }),
              });
            }
            """);
        await Page.GetByRole(AriaRole.Button, new() { Name = "History", Exact = true }).ClickAsync();
        var steps = Page.Locator(".history-sheet__step");
        await Expect(steps).ToHaveCountAsync(2);
        await steps.Last.ClickAsync();
        await Expect(Page.Locator(".shell__viewing")).ToContainTextAsync("Viewing #1");

        var blocked = Page.Locator(".details__narrator-only input");
        await Expect(blocked).ToBeDisabledAsync();
        var style = await blocked.EvaluateAsync<Style>(Look);
        Console.WriteLine($"blocked: {style}");
        Assert.Equal("0.38", style.Opacity);
        Assert.NotEqual("pointer", style.Cursor);
        Assert.NotEqual(live.Label, style.Label);
    }

    [Fact]
    public async Task An_aria_disabled_switch_looks_disabled()
    {
        await App.SeedProjectAsync("switch-aria-proof", "Switch Aria Proof", "A. Author");
        await GotoAppAsync("projects/switch-aria-proof");
        var narratorOnly = Page.GetByRole(AriaRole.Switch, new() { Name = "Narrator only" });
        await Expect(narratorOnly).ToBeEnabledAsync();
        var live = await narratorOnly.EvaluateAsync<Style>(Look);

        await narratorOnly.EvaluateAsync("el => el.setAttribute('aria-disabled', 'true')");
        var style = await narratorOnly.EvaluateAsync<Style>(Look);
        Console.WriteLine($"live: {live}\naria-disabled: {style}");
        Assert.False(style.Disabled);
        Assert.Equal("0.38", style.Opacity);
        Assert.NotEqual("pointer", style.Cursor);
        Assert.NotEqual(live.Label, style.Label);
    }
}
