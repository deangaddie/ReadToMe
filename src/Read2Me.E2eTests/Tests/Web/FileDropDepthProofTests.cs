using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #490: the drop highlight holds while a drag crosses the zone's children.</summary>
public class FileDropDepthProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task The_highlight_holds_over_a_child_and_clears_on_leaving_the_zone()
    {
        await GotoAppAsync("projects");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New project" }).First.ClickAsync();
        var zone = Page.Locator("r2m-file-drop");
        await Expect(zone).ToBeVisibleAsync();

        // Real DragEvents with a DataTransfer, in the order the browser fires them.
        var result = await zone.EvaluateAsync<string[]>("""
            zone => {
              const label = zone.querySelector('.r2m-file-drop__label');
              const dt = new DataTransfer();
              const fire = (el, type, relatedTarget = null) =>
                el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt, relatedTarget }));
              const over = () => String(zone.classList.contains('r2m-file-drop--over'));
              const seen = [];
              fire(zone, 'dragenter');
              fire(zone, 'dragover');
              seen.push('zone:' + over());
              fire(label, 'dragenter', zone);
              fire(zone, 'dragleave', label);
              seen.push('child:' + over());
              fire(label, 'dragover');
              fire(label, 'dragleave', null);
              seen.push('left:' + over());
              return seen;
            }
            """);

        Assert.Equal(["zone:true", "child:true", "left:false"], result);
    }
}
