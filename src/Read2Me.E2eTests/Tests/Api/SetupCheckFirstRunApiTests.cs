using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The report's first-run flag: set on the start that created app.db, cleared on the next. Its own
/// class, because the test restarts the host its fixture holds.
/// </summary>
public class SetupCheckFirstRunApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task The_start_that_created_app_db_is_the_first_run_and_the_next_start_is_not()
    {
        var first = await ReportAsync();
        Assert.True(first.GetProperty("firstRun").GetBoolean());

        await app.RestartAsync();

        var next = await ReportAsync();
        Assert.False(next.GetProperty("firstRun").GetBoolean());
        // The web app keys the start banner's dismissal to the host start, so each start differs.
        Assert.True(next.GetProperty("startedAt").GetDateTimeOffset() > first.GetProperty("startedAt").GetDateTimeOffset());
    }

    private async Task<JsonElement> ReportAsync()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/setup-check");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
