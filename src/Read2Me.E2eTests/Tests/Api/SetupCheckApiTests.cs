using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>/api/setup-check</c> on the in-proc host: the start-up Quick check fills the report by
/// itself, and a requested one replaces it. The rules behind each row are SetupCheckRunnerTests.
/// </summary>
public class SetupCheckApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private static readonly string[] AllIds =
    [
        "workspace", "ffmpeg", "docker",
        "llm", "attribution-chain", "paragraph-tts", "voice-design", "transcription", "similarity",
        "container-llama", "container-audiocpp", "container-whisper", "container-minilm-l6", "container-mpnet-base-v2",
    ];

    [Fact]
    public async Task The_start_up_quick_check_fills_the_report_and_a_requested_one_replaces_it()
    {
        var atStart = await ReportAfterStartAsync();
        Assert.Equal(AllIds, Ids(atStart));
        Assert.Equal(JsonValueKind.Null, atStart.GetProperty("fullCheckedAt").ValueKind);

        var workspace = atStart.GetProperty("rows")[0];
        Assert.Equal("workspaceAndTools", workspace.GetProperty("group").GetString());
        Assert.Equal("quick", workspace.GetProperty("tier").GetString());
        Assert.Equal("passed", workspace.GetProperty("result").GetString());
        Assert.Equal(app.WorkspaceDir, workspace.GetProperty("found").GetString());
        Assert.False(string.IsNullOrEmpty(workspace.GetProperty("fix").GetProperty("text").GetString()));

        var ran = await ReadAsync(await Http.PostAsync($"{app.BaseUrl}/api/setup-check/quick", null));
        Assert.Equal(AllIds, Ids(ran));
        Assert.True(QuickCheckedAt(ran) > QuickCheckedAt(atStart));

        var read = await ReadAsync(await Http.GetAsync($"{app.BaseUrl}/api/setup-check"));
        Assert.Equal(QuickCheckedAt(ran), QuickCheckedAt(read));
    }

    /// <summary>The start-up check runs in the background, so the report may still be empty at first.</summary>
    private async Task<JsonElement> ReportAfterStartAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var report = await ReadAsync(await Http.GetAsync($"{app.BaseUrl}/api/setup-check"));
            if (report.GetProperty("quickCheckedAt").ValueKind != JsonValueKind.Null)
                return report;
            Assert.Empty(report.GetProperty("rows").EnumerateArray());
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The start-up Quick check had not finished after 30 s.");
            await Task.Delay(100);
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string?[] Ids(JsonElement report) =>
        report.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToArray();

    private static DateTimeOffset QuickCheckedAt(JsonElement report) =>
        report.GetProperty("quickCheckedAt").GetDateTimeOffset();
}
