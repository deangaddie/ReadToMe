using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The Voice rules reads behind the cast page (Angular ticket 17): a character's rule list and the
/// per-chapter resolved-voice preview, driven through the existing rule commands.
/// </summary>
public class VoiceRulesApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<Guid> RunAsync(string folder, object command)
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands", command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("newEntityId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetGuid()
            : Guid.Empty;
    }

    private Task<List<JsonElement>> RulesAsync(string folder, Guid characterId) =>
        ListAsync($"/api/projects/{folder}/characters/{characterId}/voice-rules");

    private Task<List<JsonElement>> PreviewAsync(string folder, Guid characterId) =>
        ListAsync($"/api/projects/{folder}/characters/{characterId}/voice-rules/preview");

    private async Task<List<JsonElement>> ListAsync(string path) =>
        (await GetJsonAsync(path)).EnumerateArray().ToList();

    /// <summary>
    /// One CreateVoiceRule and the wire shapes of both reads. Which voice a rule picks per chapter,
    /// and the list after a move or a delete, are VoiceRulePreviewTests and
    /// VoiceLifecycleMutationTests.
    /// </summary>
    [Fact]
    public async Task A_created_rule_reads_back_in_both_wire_shapes_and_an_unknown_project_is_404()
    {
        var folder = $"api-voice-rules-{Guid.NewGuid():N}";
        var book = await app.SeedMultiChapterProjectAsync(folder, "Rules Book", "Author");
        var alice = book.CharacterId("Alice");
        await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice A", isGenerated = true });
        var voiceB = await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice B", isGenerated = true });

        var chapter3 = book.ChapterId("Chapter 3");
        var onward = await RunAsync(folder, new
        {
            type = "CreateVoiceRule", characterId = alice, voiceId = voiceB,
            fromLevel = "Chapter", fromNodeId = chapter3,
        });

        var rule = (await RulesAsync(folder, alice)).Single(r => r.GetProperty("ruleId").GetGuid() == onward);
        Assert.Equal(voiceB, rule.GetProperty("voiceId").GetGuid());
        Assert.Equal("Voice B", rule.GetProperty("voiceName").GetString());
        Assert.False(rule.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Chapter", rule.GetProperty("fromLevel").GetString());
        Assert.Equal(chapter3, rule.GetProperty("fromNodeId").GetGuid());
        Assert.Equal("Chapter 3", rule.GetProperty("fromTitle").GetString());
        Assert.False(rule.GetProperty("fromDangling").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("toLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("toNodeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("toTitle").ValueKind);
        Assert.False(rule.GetProperty("toDangling").GetBoolean());
        Assert.Equal(JsonValueKind.String, rule.GetProperty("order").ValueKind);

        var row = (await PreviewAsync(folder, alice)).Single(r => r.GetProperty("chapterId").GetGuid() == chapter3);
        Assert.Equal("Chapter 3", row.GetProperty("chapterTitle").GetString());
        Assert.Equal("Voice B", row.GetProperty("voiceName").GetString());

        // An unknown character has no rules; an unknown project is a 404.
        Assert.Empty(await RulesAsync(folder, Guid.NewGuid()));
        var missingProject = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-{Guid.NewGuid():N}/characters/{alice}/voice-rules");
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
    }
}
