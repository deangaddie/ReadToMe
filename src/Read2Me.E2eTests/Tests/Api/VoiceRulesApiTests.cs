using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The Voice rules reads behind the cast page (Angular ticket 17): a character's rule list and the
/// per-chapter resolved-voice preview, driven through the existing rule commands.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceRulesApiTests(E2eAppFixture app)
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

    private static string[] VoiceNames(List<JsonElement> preview) =>
        preview.Select(r => r.GetProperty("voiceName").GetString() ?? "").ToArray();

    [Fact]
    public async Task Rules_and_preview_follow_create_move_delete()
    {
        var folder = $"api-voice-rules-{Guid.NewGuid():N}";
        var book = await app.SeedMultiChapterProjectAsync(folder, "Rules Book", "Author");
        var alice = book.CharacterId("Alice");

        // No voices yet: no rules, but every chapter is listed with no voice.
        Assert.Empty(await RulesAsync(folder, alice));
        var empty = await PreviewAsync(folder, alice);
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3", "Chapter 4", "Chapter 5"],
            empty.Select(r => r.GetProperty("chapterTitle").GetString()!).ToArray());
        Assert.Equal(book.ChapterId("Chapter 3"), empty[2].GetProperty("chapterId").GetGuid());
        Assert.All(empty, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("voiceName").ValueKind));

        // The first voice brings the default rule.
        var voiceA = await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice A", isGenerated = true });
        var voiceB = await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice B", isGenerated = true });
        var rules = await RulesAsync(folder, alice);
        var @default = Assert.Single(rules);
        Assert.True(@default.GetProperty("isDefault").GetBoolean());
        Assert.Equal(voiceA, @default.GetProperty("voiceId").GetGuid());
        Assert.Equal("Voice A", @default.GetProperty("voiceName").GetString());
        Assert.Equal(JsonValueKind.Null, @default.GetProperty("fromLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, @default.GetProperty("toLevel").ValueKind);
        Assert.False(@default.GetProperty("fromDangling").GetBoolean());
        Assert.Equal(["Voice A", "Voice A", "Voice A", "Voice A", "Voice A"], VoiceNames(await PreviewAsync(folder, alice)));

        // "From Chapter 3 onward → Voice B": the preview flips at chapter 3.
        var chapter3 = book.ChapterId("Chapter 3");
        var onward = await RunAsync(folder, new
        {
            type = "CreateVoiceRule", characterId = alice, voiceId = voiceB,
            fromLevel = "Chapter", fromNodeId = chapter3,
        });
        rules = await RulesAsync(folder, alice);
        Assert.Equal(2, rules.Count);
        Assert.True(rules[0].GetProperty("isDefault").GetBoolean());
        var rule = rules[1];
        Assert.Equal(onward, rule.GetProperty("ruleId").GetGuid());
        Assert.False(rule.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Chapter", rule.GetProperty("fromLevel").GetString());
        Assert.Equal(chapter3, rule.GetProperty("fromNodeId").GetGuid());
        Assert.Equal("Chapter 3", rule.GetProperty("fromTitle").GetString());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("toLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("toNodeId").ValueKind);
        Assert.True(string.CompareOrdinal(rules[0].GetProperty("order").GetString(), rule.GetProperty("order").GetString()) < 0);
        Assert.Equal(["Voice A", "Voice A", "Voice B", "Voice B", "Voice B"], VoiceNames(await PreviewAsync(folder, alice)));

        // "Just Chapter 4 → Voice A" after it: the later rule wins on chapter 4 only.
        var chapter4 = book.ChapterId("Chapter 4");
        var single = await RunAsync(folder, new
        {
            type = "CreateVoiceRule", characterId = alice, voiceId = voiceA,
            fromLevel = "Chapter", fromNodeId = chapter4, toLevel = "Chapter", toNodeId = chapter4,
        });
        Assert.Equal(["Voice A", "Voice A", "Voice B", "Voice A", "Voice B"], VoiceNames(await PreviewAsync(folder, alice)));

        // Moving the single-chapter rule above the onward rule lets the onward rule win everywhere.
        await RunAsync(folder, new { type = "MoveVoiceRule", ruleId = single, direction = "Up" });
        rules = await RulesAsync(folder, alice);
        Assert.Equal([@default.GetProperty("ruleId").GetGuid(), single, onward],
            rules.Select(r => r.GetProperty("ruleId").GetGuid()).ToArray());
        Assert.Equal(["Voice A", "Voice A", "Voice B", "Voice B", "Voice B"], VoiceNames(await PreviewAsync(folder, alice)));

        // Deleting the onward rule returns everything to the default.
        await RunAsync(folder, new { type = "DeleteVoiceRule", ruleId = onward });
        rules = await RulesAsync(folder, alice);
        Assert.Equal(2, rules.Count);
        Assert.Equal(["Voice A", "Voice A", "Voice A", "Voice A", "Voice A"], VoiceNames(await PreviewAsync(folder, alice)));
    }

    [Fact]
    public async Task Rule_anchored_to_a_deleted_chapter_is_dangling_and_skipped()
    {
        var folder = $"api-voice-rules-dangling-{Guid.NewGuid():N}";
        var book = await app.SeedMultiChapterProjectAsync(folder, "Dangling Book", "Author", chapters: 3);
        var alice = book.CharacterId("Alice");
        var voiceA = await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice A", isGenerated = true });
        var voiceB = await RunAsync(folder, new { type = "CreateVoice", characterId = alice, name = "Voice B", isGenerated = true });
        var chapter2 = book.ChapterId("Chapter 2");
        await RunAsync(folder, new
        {
            type = "CreateVoiceRule", characterId = alice, voiceId = voiceB,
            fromLevel = "Chapter", fromNodeId = chapter2, toLevel = "Chapter", toNodeId = chapter2,
        });
        Assert.Equal(["Voice A", "Voice B", "Voice A"], VoiceNames(await PreviewAsync(folder, alice)));

        await RunAsync(folder, new { type = "DeleteChapter", chapterId = chapter2 });

        var rule = (await RulesAsync(folder, alice)).Single(r => !r.GetProperty("isDefault").GetBoolean());
        Assert.True(rule.GetProperty("fromDangling").GetBoolean());
        Assert.True(rule.GetProperty("toDangling").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("fromTitle").ValueKind);
        Assert.Equal(chapter2, rule.GetProperty("fromNodeId").GetGuid());
        Assert.Equal("Chapter", rule.GetProperty("fromLevel").GetString());
        Assert.Equal(["Voice A", "Voice A"], VoiceNames(await PreviewAsync(folder, alice)));
        Assert.Equal(voiceA, (await RulesAsync(folder, alice))[0].GetProperty("voiceId").GetGuid());
    }

    [Fact]
    public async Task Unknown_project_is_404_and_unknown_character_is_empty()
    {
        var folder = $"api-voice-rules-404-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Missing Book", "Author", chapters: 2);

        var missingProject = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-{Guid.NewGuid():N}/characters/{Guid.NewGuid()}/voice-rules");
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
        var missingPreview = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-{Guid.NewGuid():N}/characters/{Guid.NewGuid()}/voice-rules/preview");
        Assert.Equal(HttpStatusCode.NotFound, missingPreview.StatusCode);

        Assert.Empty(await RulesAsync(folder, Guid.NewGuid()));
        var preview = await PreviewAsync(folder, Guid.NewGuid());
        Assert.Equal(2, preview.Count);
        Assert.All(preview, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("voiceName").ValueKind));
    }
}
