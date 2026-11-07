using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.App.Configuration;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>/api/setup-check</c> on the in-proc host: the start-up Quick check fills the report by
/// itself, and a requested one replaces it. The rows (their ids, order, results and notes) are the
/// rules of SetupCheckRunnerTests; here only their wire fields are read.
/// </summary>
public class SetupCheckApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task The_start_up_quick_check_fills_the_report_and_a_requested_one_replaces_it()
    {
        var atStart = await ReportAfterStartAsync();
        Assert.NotEmpty(atStart.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, atStart.GetProperty("fullCheckedAt").ValueKind);
        Assert.False(atStart.GetProperty("noWorkspace").GetBoolean());
        // With a Workspace, the hosted services that touch app.db run.
        Assert.All(app.Services.GetServices<IHostedService>().OfType<WorkspaceHostedService>(), s => Assert.NotNull(s.Running));

        // The wire fields of a row, as names (enums go out as their member names).
        foreach (var row in atStart.GetProperty("rows").EnumerateArray())
            foreach (var field in new[] { "id", "group", "tier", "result" })
                Assert.Equal(JsonValueKind.String, row.GetProperty(field).ValueKind);

        var ran = await ReadAsync(await Http.PostAsync($"{app.BaseUrl}/api/setup-check/quick", null));
        Assert.Equal(Ids(atStart), Ids(ran));
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
