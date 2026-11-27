using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #472. Playwright cannot hear a screen reader, so this checks the wiring in
/// the real engine: the browser has <c>ariaNotify</c>, <c>announce()</c> lands on the open modal
/// (else the document) with the right priority, and <c>.visually-hidden</c> content is out of view,
/// in the accessibility tree and visible under focus. Needs the temporary <c>window.__announce</c>
/// hook in <c>main.ts</c>.
/// </summary>
public class AnnouncerProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Records every ariaNotify call, then makes the real one, so a bad option would still throw.
    private const string Recorder = """
        window.__calls = [];
        for (const proto of [Document.prototype, Element.prototype]) {
          const real = proto.ariaNotify;
          if (!real) continue;
          proto.ariaNotify = function (text, options) {
            window.__calls.push({ target: this, text, priority: options?.priority });
            return real.call(this, text, options);
          };
        }
        """;

    private const string LastCall = """
        () => {
          const call = window.__calls.at(-1);
          const target = call.target === document ? 'document'
            : call.target === document.querySelector('dialog[open]:modal') ? 'open modal dialog'
            : call.target.nodeName;
          return `${window.__calls.length} | ${target} | ${call.text} | ${call.priority}`;
        }
        """;

    // Needs a real browser: ariaNotify and :modal exist only in a real engine.
    [Fact]
    public async Task Announce_lands_on_the_open_modal_else_the_document()
    {
        var book = await App.SeedLongBookAsync("announcer-proof", "Announcer Proof", "A. Author", chapters: 2, paragraphs: 2);
        await Page.AddInitScriptAsync(Recorder);
        await GotoAppAsync("projects/announcer-proof/book");
        await Expect(Page.Locator(".tree__title")).ToHaveCountAsync(2);

        Assert.True(await Page.EvaluateAsync<bool>("() => 'ariaNotify' in Element.prototype"), "no Element.ariaNotify");
        Assert.True(await Page.EvaluateAsync<bool>("() => 'ariaNotify' in Document.prototype"), "no Document.ariaNotify");

        await Page.EvaluateAsync("() => window.__announce('before the dialog')");
        Assert.Equal("1 | document | before the dialog | normal", await Page.EvaluateAsync<string>(LastCall));

        await Page.Locator($"[data-node-id='{book.ChapterId("Chapter 2")}']").FocusAsync();
        await Page.Keyboard.PressAsync("Shift+F10");
        await Expect(Page.GetByRole(AriaRole.Menu, new() { Name = "Actions for Chapter 2" })).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("End");
        await Page.Keyboard.PressAsync("Enter");
        var confirm = Page.GetByRole(AriaRole.Dialog);
        await Expect(confirm).ToBeVisibleAsync();

        await Page.EvaluateAsync("() => window.__announce('over the dialog', 'assertive')");
        Assert.Equal("2 | open modal dialog | over the dialog | high", await Page.EvaluateAsync<string>(LastCall));
        await Page.EvaluateAsync("() => window.__announce('politely over the dialog', 'polite')");
        Assert.Equal("3 | open modal dialog | politely over the dialog | normal", await Page.EvaluateAsync<string>(LastCall));

        await confirm.Locator(".r2m-confirm-dialog__cancel").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync();
        await Page.EvaluateAsync("() => window.__announce('after the dialog', 'assertive')");
        Assert.Equal("4 | document | after the dialog | high", await Page.EvaluateAsync<string>(LastCall));

        TestContext.Current.TestOutputHelper?.WriteLine($"{Page.Context.Browser!.BrowserType.Name} {Page.Context.Browser.Version}: ariaNotify wired");
    }

    // Needs a real browser: clipping, size and focus styles are computed by a real engine.
    [Fact]
    public async Task Visually_hidden_content_is_unseen_named_and_shown_under_focus()
    {
        await GotoAppAsync("projects");
        await Page.EvaluateAsync("""
            () => document.body.insertAdjacentHTML('afterbegin', `
              <div id="wrapper" class="visually-hidden"><a href="#main">Skip to the proof content</a></div>
              <a id="self" class="visually-hidden" href="#main">Skip, hidden on itself</a>
              <button id="named" type="button">Edit <span class="visually-hidden">the chapter title</span></button>`)
            """);

        var wrapper = Page.Locator("#wrapper");
        var self = Page.Locator("#self");
        var span = Page.Locator("#named span");

        foreach (var hidden in new[] { wrapper, self, span })
        {
            await Expect(hidden).ToHaveCSSAsync("position", "absolute");
            await Expect(hidden).ToHaveCSSAsync("clip-path", "inset(50%)");
            var box = (await hidden.BoundingBoxAsync())!;
            Assert.True(box.Width <= 1 && box.Height <= 1, $"{box.Width}x{box.Height}");
        }

        var snapshot = await Page.Locator("body").AriaSnapshotAsync();
        Assert.Contains("link \"Skip to the proof content\"", snapshot);
        Assert.Contains("link \"Skip, hidden on itself\"", snapshot);
        Assert.Contains("button \"Edit the chapter title\"", snapshot);

        // Something inside it has focus.
        await wrapper.Locator("a").FocusAsync();
        await Expect(wrapper).ToHaveCSSAsync("clip-path", "none");
        await Expect(wrapper).ToHaveCSSAsync("position", "static");
        Assert.True((await wrapper.BoundingBoxAsync())!.Width > 100);

        // It has focus itself; the one that lost it hides again.
        await self.FocusAsync();
        await Expect(self).ToHaveCSSAsync("clip-path", "none");
        Assert.True((await self.BoundingBoxAsync())!.Width > 100);
        await Expect(wrapper).ToHaveCSSAsync("clip-path", "inset(50%)");

        TestContext.Current.TestOutputHelper?.WriteLine($"{Page.Context.Browser!.BrowserType.Name} {Page.Context.Browser.Version}: .visually-hidden proven");
    }
}
