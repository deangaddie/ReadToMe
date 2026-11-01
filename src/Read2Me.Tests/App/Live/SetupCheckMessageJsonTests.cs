using System.Text.Json;
using Read2Me.App.Live;
using Read2Me.App.Services.SetupCheck;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// A <c>setupCheck</c> message carries the report the web app also reads over REST, so its enums
/// take the one wire form under the hub's options too: their member names.
/// </summary>
public class SetupCheckMessageJsonTests
{
    [Fact]
    public void The_report_s_enums_are_member_names_on_the_hub_as_on_the_REST_wire()
    {
        var options = new JsonSerializerOptions();
        LiveServiceCollectionExtensions.ConfigurePayloadJson(options);
        var row = new SetupCheckRow(
            SetupCheckIds.Ffmpeg, SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Full,
            SetupCheckResult.NotConfirmed, "e", "f", null, new SetupFixHint("Fix it.", null, null), Managed: false, Rerun: null);
        var report = SetupCheckReport.Empty with
        {
            Rows = [row],
            FullRun = new SetupFullRun(1, 1, SetupCheckLane.Local, "Encoding a 1 s tone to .m4b", Cancelling: false),
        };

        var json = JsonSerializer.SerializeToElement(LiveMessageMapper.SetupCheckProgress(report), options);

        var wire = json.GetProperty("report");
        var wireRow = wire.GetProperty("rows")[0];
        Assert.Equal("WorkspaceAndTools", wireRow.GetProperty("group").GetString());
        Assert.Equal("Full", wireRow.GetProperty("tier").GetString());
        Assert.Equal("NotConfirmed", wireRow.GetProperty("result").GetString());
        Assert.Equal("Local", wire.GetProperty("fullRun").GetProperty("lane").GetString());
    }
}
