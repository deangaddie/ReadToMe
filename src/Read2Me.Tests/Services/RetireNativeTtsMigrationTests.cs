using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;
using Read2Me.AppData.Migrations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// audiocpp-tts 09 (spec D7): stored TTS and voice-design configs pointing at a retired native
    /// Python service are repointed at the audio.cpp container, and Chatterbox Turbo rows go. Rows are
    /// seeded with raw SQL in the shapes real installs hold — Type as the enum name, SettingsJson in
    /// whichever key casing the form that wrote it used.
    /// </summary>
    public class RetireNativeTtsMigrationTests : AppDbMigrationTestBase
    {
        private const string AudioCpp = "http://localhost:8004";

        [Fact]
        public async Task LocalVoxCpm2Config_IsRepointedAtAudioCpp_WithDefaultModelId()
        {
            await SeedAsync(
                """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'vox', 'VoxCpm2', '{"baseUrl":"http://localhost:8003","cfg_value":2,"maxChunkChars":1500}')""");

            var json = await MigrateAndReadAsync("ParagraphTtsServiceConfigs", 1);

            Assert.Equal(AudioCpp, (string?)json["baseUrl"]);
            Assert.Equal("voxcpm2", (string?)json["modelId"]);
            // Everything else in the blob is the user's and stays.
            Assert.Equal(2, (int?)json["cfg_value"]);
            Assert.Equal(1500, (int?)json["maxChunkChars"]);
        }

        [Theory]
        [InlineData("Chatterbox", "http://localhost:8000", "chatterbox")]
        [InlineData("Qwen3Base", "http://127.0.0.1:8101/", "qwen3-base")]
        [InlineData("VoxCpm2", "HTTP://LOCALHOST:8003", "voxcpm2")]
        // A config pointed at the wrong native service still moves; the model follows its Type.
        [InlineData("Chatterbox", "http://localhost:8100", "chatterbox")]
        public async Task LocalParagraphTtsConfigs_OnAnyNativePort_AreRepointed(string type, string baseUrl, string modelId)
        {
            await SeedAsync(
                $$"""INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'x', '{{type}}', '{"baseUrl":"{{baseUrl}}"}')""");

            var json = await MigrateAndReadAsync("ParagraphTtsServiceConfigs", 1);

            Assert.Equal(AudioCpp, (string?)json["baseUrl"]);
            Assert.Equal(modelId, (string?)json["modelId"]);
        }

        [Fact]
        public async Task RepointedConfig_ThatAlreadyNamesAModel_KeepsIt()
        {
            await SeedAsync(
                """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'q', 'Qwen3Base', '{"baseUrl":"http://localhost:8101","modelId":"qwen3-base-custom"}')""");

            var json = await MigrateAndReadAsync("ParagraphTtsServiceConfigs", 1);

            Assert.Equal(AudioCpp, (string?)json["baseUrl"]);
            Assert.Equal("qwen3-base-custom", (string?)json["modelId"]);
        }

        [Theory]
        [InlineData("http://gpu-box:8003")]
        [InlineData("http://192.168.1.20:8000")]
        [InlineData("http://localhost:8004")] // already audio.cpp
        [InlineData("http://localhost:9003")]
        public async Task ConfigsNotOnALocalNativePort_AreLeftAlone(string baseUrl)
        {
            var settings = $$"""{"baseUrl":"{{baseUrl}}","maxChunkChars":500}""";
            await SeedAsync(
                $"INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'c', 'Chatterbox', '{settings}')");

            var json = await MigrateAndReadAsync("ParagraphTtsServiceConfigs", 1);

            Assert.Equal(baseUrl, (string?)json["baseUrl"]);
            Assert.Null(json["modelId"]);
        }

        [Fact]
        public async Task PascalCaseBlob_IsRepointedUnderItsOwnKey()
        {
            // The VoxCPM2 voice-design record names BaseUrl without an attribute, so a plain
            // Serialize stored it PascalCase; ModelId is named "modelId" on that record.
            await SeedAsync(
                """INSERT INTO VoiceDesignServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'v', 'VoxCpm2', '{"BaseUrl":"http://localhost:8003","cfg_value":2}')""");

            var json = await MigrateAndReadAsync("VoiceDesignServiceConfigs", 1);

            Assert.Equal(AudioCpp, (string?)json["BaseUrl"]);
            Assert.Null(json["baseUrl"]);
            Assert.Equal("voxcpm2", (string?)json["modelId"]);
        }

        [Fact]
        public async Task Qwen3VoiceDesign_IsRepointed_WithPascalCaseModelId()
        {
            // Qwen3VoiceDesignSettings has no JSON names: its blobs are stored PascalCase, so the
            // repoint must keep "ModelId" in that case.
            await SeedAsync(
                """INSERT INTO VoiceDesignServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'q', 'Qwen3', '{"BaseUrl":"http://localhost:8100","Language":"English"}')""");

            var json = await MigrateAndReadAsync("VoiceDesignServiceConfigs", 1);

            Assert.Equal(AudioCpp, (string?)json["BaseUrl"]);
            Assert.Equal("qwen3-design", (string?)json["ModelId"]);
            Assert.Null(json["modelId"]);
            Assert.Equal("English", (string?)json["Language"]);
        }

        [Fact]
        public async Task MalformedSettingsJson_IsSkipped_NotFatal()
        {
            await SeedAsync(
                """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'bad', 'VoxCpm2', 'not json')""",
                """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (2, 'vox', 'VoxCpm2', '{"baseUrl":"http://localhost:8003"}')""");

            await using var db = await OpenDbAsync();

            Assert.Equal("not json", await ScalarAsync(db, "SELECT SettingsJson FROM ParagraphTtsServiceConfigs WHERE Id = 1"));
            var repointed = JsonNode.Parse((string)(await ScalarAsync(db, "SELECT SettingsJson FROM ParagraphTtsServiceConfigs WHERE Id = 2"))!)!;
            Assert.Equal(AudioCpp, (string?)repointed["baseUrl"]);
        }

        [Fact]
        public async Task VoxCpm2Configs_LoseNormalizeAndDenoise()
        {
            // audio.cpp has neither knob (bt-07). Dropped from every VoxCPM2 config, remote ones too:
            // no client reads them any more.
            await SeedAsync(
                """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'vox', 'VoxCpm2', '{"baseUrl":"http://localhost:8003","normalize":true,"denoise":false,"cfg_value":2}')""",
                """INSERT INTO VoiceDesignServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'vox', 'VoxCpm2', '{"BaseUrl":"http://gpu-box:8003","Normalize":true,"Denoise":true}')""",
                """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (2, 'other', 'Chatterbox', '{"baseUrl":"http://gpu-box:8000","normalize":true}')""");

            var tts = await MigrateAndReadAsync("ParagraphTtsServiceConfigs", 1);
            var design = await MigrateAndReadAsync("VoiceDesignServiceConfigs", 1);
            var other = await MigrateAndReadAsync("ParagraphTtsServiceConfigs", 2);

            Assert.False(tts.ContainsKey("normalize"));
            Assert.False(tts.ContainsKey("denoise"));
            Assert.Equal(2, (int?)tts["cfg_value"]);
            Assert.False(design.ContainsKey("Normalize"));
            Assert.False(design.ContainsKey("Denoise"));
            Assert.Equal("http://gpu-box:8003", (string?)design["BaseUrl"]);
            // Only VoxCPM2 ever had those knobs; another type's blob is not VoxCPM2's to clean.
            Assert.True(other.ContainsKey("normalize"));
        }

        [Theory]
        [InlineData(1, null)]  // active config was Turbo -> nothing active
        [InlineData(3, 3)]     // active config was something else -> untouched
        public async Task TurboConfigs_AreDeleted_WithTheirSteps_AndClearedAsActive(int activeId, int? expectedActive)
        {
            await using (var before = await OpenDbAtAsync(nameof(OverwriteAttributionPromptsForFrozenItems)))
            {
                // 'ChatterboxTurbo' is what EF stored while the member existed; '2' is how the
                // reserved value would round-trip now that it does not.
                await ExecAsync(before, """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (1, 'turbo', 'ChatterboxTurbo', '{"BaseUrl":"http://localhost:8001"}')""");
                await ExecAsync(before, """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (2, 'turbo2', '2', '{"BaseUrl":"http://localhost:8001"}')""");
                await ExecAsync(before, """INSERT INTO ParagraphTtsServiceConfigs (Id, Name, Type, SettingsJson) VALUES (3, 'breeze', 'Breeze', '{"baseUrl":"http://localhost:8004"}')""");
                await ExecAsync(before, "INSERT INTO TextSubstitutionSteps (Id, ParagraphTtsServiceConfigId, FromText, ToText, \"Order\") VALUES ('s1', 1, 'a', 'b', 0)");
                await ExecAsync(before, "INSERT INTO TextSubstitutionSteps (Id, ParagraphTtsServiceConfigId, FromText, ToText, \"Order\") VALUES ('s3', 3, 'a', 'b', 0)");
                await ExecAsync(before, "INSERT INTO ToSentenceCaseConfigs (ParagraphTtsServiceConfigId, ParagraphEnabled, WordEnabled, WordMinLength) VALUES (1, 1, 0, 4)");
                before.Settings.Add(new() { ActiveParagraphTtsConfigId = activeId });
                await before.SaveChangesAsync();
            }

            await using var db = await OpenDbAsync();

            Assert.Equal("3", await ScalarAsync(db, "SELECT group_concat(Id) FROM ParagraphTtsServiceConfigs"));
            Assert.Equal("s3", await ScalarAsync(db, "SELECT group_concat(Id) FROM TextSubstitutionSteps"));
            Assert.Equal(0L, await ScalarAsync(db, "SELECT count(*) FROM ToSentenceCaseConfigs"));
            Assert.Equal(expectedActive, (await db.Settings.SingleAsync()).ActiveParagraphTtsConfigId);
        }

        // --- helpers ---

        private async Task SeedAsync(params string[] statements)
        {
            // nameof, not the timestamped id: a migration inserted before this one would silently
            // move the seed point, and the compiler cannot see a string drift.
            await using var before = await OpenDbAtAsync(nameof(OverwriteAttributionPromptsForFrozenItems));
            foreach (var sql in statements)
                await ExecAsync(before, sql);
        }

        private async Task<JsonObject> MigrateAndReadAsync(string table, int id)
        {
            await using var db = await OpenDbAsync();
            var json = await ScalarAsync(db, $"SELECT SettingsJson FROM {table} WHERE Id = {id}");
            Assert.NotNull(json);
            return JsonNode.Parse((string)json!)!.AsObject();
        }

        private static async Task ExecAsync(Read2MeDbContext db, string sql)
        {
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<object?> ScalarAsync(Read2MeDbContext db, string sql)
        {
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = await cmd.ExecuteScalarAsync();
            return result is DBNull ? null : result;
        }
    }
}
