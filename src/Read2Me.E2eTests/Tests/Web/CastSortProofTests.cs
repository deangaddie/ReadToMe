using System.Net;
using System.Net.Http.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #499: changing the Cast sort makes one <c>ariaNotify</c> call naming the
/// result, and names with numbers sort naturally ("Guard 2" before "Guard 10").
/// </summary>
public class CastSortProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    // Records every ariaNotify call, then makes the real one, so a bad option would still throw.
    private const string Recorder = """
        window.__calls = [];
        for (const proto of [Document.prototype, Element.prototype]) {
          const real = proto.ariaNotify;
          if (!real) continue;
          proto.ariaNotify = function (text, options) {
            window.__calls.push(`${this === document ? 'document' : this.nodeName} | ${text} | ${options?.priority}`);
            return real.call(this, text, options);
          };
        }
        """;

    // Needs a real browser: ariaNotify exists only in a real engine.
    [Fact]
    public async Task Changing_the_sort_announces_once_and_numbers_sort_naturally()
    {
        const string folder = "cast-sort-proof";
        await App.SeedProjectAsync(folder, "Cast Sort Proof", "A. Author");
        foreach (var name in new[] { "Guard 10", "Guard 2" })
        {
            var response = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{folder}/commands", new { type = "CreateCharacter", name });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await Page.AddInitScriptAsync(Recorder);
        await GotoAppAsync($"projects/{folder}/cast");
        var names = Page.Locator(".cast__rows .cast__name");
        await Expect(names).ToHaveTextAsync(["Narrator", "Alice", "Guard 2", "Guard 10"]);
        Assert.Empty(await Page.EvaluateAsync<string[]>("() => window.__calls"));

        await Page.GetByLabel("Sort", new() { Exact = true }).SelectOptionAsync("lines");
        await Expect(Page.Locator(".cast__sort select")).ToHaveValueAsync("lines");
        await Page.WaitForFunctionAsync("() => window.__calls.length > 0");
        Assert.Equal(["document | Sorted by lines, 4 characters | normal"], await Page.EvaluateAsync<string[]>("() => window.__calls"));

        await Page.GetByRole(Microsoft.Playwright.AriaRole.Searchbox, new() { Name = "Search characters" }).FillAsync("guard");
        await Expect(names).ToHaveTextAsync(["Guard 2", "Guard 10"]);
        await Page.GetByLabel("Sort", new() { Exact = true }).SelectOptionAsync("name");
        await Page.WaitForFunctionAsync("() => window.__calls.length > 1");
        Assert.Equal(
            ["document | Sorted by lines, 4 characters | normal", "document | Sorted by name, 2 characters | normal"],
            await Page.EvaluateAsync<string[]>("() => window.__calls"));
        await Expect(names).ToHaveTextAsync(["Guard 2", "Guard 10"]);

        TestContext.Current.TestOutputHelper?.WriteLine($"{Page.Context.Browser!.BrowserType.Name} {Page.Context.Browser.Version}: {string.Join(" / ", await Page.EvaluateAsync<string[]>("() => window.__calls"))}");
    }
}
