using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Themes over HTTP: list, custom CRUD, built-in protection, and the shared selection
/// (selected theme + follow-system flag) the Blazor UI reads too.
/// </summary>
[Collection(E2eCollection.Name)]
public class ThemesApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task List_contains_built_in_light_and_dark_first()
    {
        var themes = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes"))
            .RootElement.EnumerateArray().ToList();

        Assert.Contains(themes, t => t.GetProperty("name").GetString() == "Light" && t.GetProperty("isBuiltIn").GetBoolean());
        Assert.Contains(themes, t => t.GetProperty("name").GetString() == "Dark" && t.GetProperty("isDark").GetBoolean());
        Assert.True(themes.First().GetProperty("isBuiltIn").GetBoolean());
    }

    [Fact]
    public async Task Custom_theme_create_update_delete_roundtrip()
    {
        var name = $"theme-{Guid.NewGuid():N}";

        var create = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/themes",
            new { name, isDark = true, primary = "#ff9900", secondary = "#00aaff", background = "  " });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.False(created.GetProperty("isBuiltIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("background").ValueKind); // blank → null
        Assert.Equal($"/api/settings/themes/{id}", create.Headers.Location?.ToString());

        var update = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/{id}",
            new { id, name, isDark = false, primary = "#123456", secondary = "#abcdef", isBuiltIn = true });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var listed = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes"))
            .RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id);
        Assert.Equal("#123456", listed.GetProperty("primary").GetString());
        Assert.False(listed.GetProperty("isDark").GetBoolean());
        Assert.False(listed.GetProperty("isBuiltIn").GetBoolean()); // a client cannot promote a theme to built-in

        var delete = await Http.DeleteAsync($"{app.BaseUrl}/api/settings/themes/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var again = await Http.DeleteAsync($"{app.BaseUrl}/api/settings/themes/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Built_in_themes_cannot_be_edited_or_deleted()
    {
        var light = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes"))
            .RootElement.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Light");
        var id = light.GetProperty("id").GetInt32();

        var update = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/{id}",
            new { id, name = "Hacked", primary = "#000000", secondary = "#000000" });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Contains("built-in", await update.Content.ReadAsStringAsync());

        var delete = await Http.DeleteAsync($"{app.BaseUrl}/api/settings/themes/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);

        var still = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes"))
            .RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id);
        Assert.Equal("Light", still.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Invalid_colour_or_missing_name_is_400()
    {
        var badColour = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/themes",
            new { name = "x", primary = "orange", secondary = "#00aaff" });
        Assert.Equal(HttpStatusCode.BadRequest, badColour.StatusCode);
        Assert.Contains("primary", await badColour.Content.ReadAsStringAsync());

        var noName = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/themes",
            new { name = " ", primary = "#ff9900", secondary = "#00aaff" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
    }

    [Fact]
    public async Task Selection_get_and_put_roundtrip_with_partial_updates()
    {
        var original = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes/selection")).RootElement;
        var originalId = original.GetProperty("selectedThemeId").ValueKind == JsonValueKind.Null
            ? (int?)null
            : original.GetProperty("selectedThemeId").GetInt32();
        var originalFollow = original.GetProperty("followSystemPreference").GetBoolean();

        var dark = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes"))
            .RootElement.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Dark")
            .GetProperty("id").GetInt32();
        try
        {
            // Only the theme: follow flag untouched.
            var put = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/selection", new { selectedThemeId = dark });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var after = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(dark, after.GetProperty("selectedThemeId").GetInt32());
            Assert.Equal(originalFollow, after.GetProperty("followSystemPreference").GetBoolean());

            // Only the flag: theme untouched.
            var flag = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/selection", new { followSystemPreference = !originalFollow });
            var afterFlag = JsonDocument.Parse(await flag.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(dark, afterFlag.GetProperty("selectedThemeId").GetInt32());
            Assert.Equal(!originalFollow, afterFlag.GetProperty("followSystemPreference").GetBoolean());

            var get = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes/selection")).RootElement;
            Assert.Equal(dark, get.GetProperty("selectedThemeId").GetInt32());

            var unknown = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/selection", new { selectedThemeId = 999999 });
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/selection",
                new { selectedThemeId = originalId, followSystemPreference = originalFollow });
        }
    }

    [Fact]
    public async Task Deleting_the_selected_custom_theme_clears_the_selection()
    {
        var original = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes/selection")).RootElement;
        var originalId = original.GetProperty("selectedThemeId").ValueKind == JsonValueKind.Null
            ? (int?)null
            : original.GetProperty("selectedThemeId").GetInt32();
        try
        {
            var create = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/themes",
                new { name = $"tmp-{Guid.NewGuid():N}", primary = "#ff9900", secondary = "#00aaff" });
            var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
            await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/selection", new { selectedThemeId = id });

            await Http.DeleteAsync($"{app.BaseUrl}/api/settings/themes/{id}");

            var selection = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/themes/selection")).RootElement;
            Assert.Equal(JsonValueKind.Null, selection.GetProperty("selectedThemeId").ValueKind);
        }
        finally
        {
            if (originalId is { } restore)
                await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/themes/selection", new { selectedThemeId = restore });
        }
    }
}
