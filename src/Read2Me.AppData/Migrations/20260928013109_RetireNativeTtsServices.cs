using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Read2Me.AppData.Migrations
{
    /// <summary>
    /// ADR 0010 / audiocpp-tts 09 (spec D7): the native Python TTS containers are retired and every
    /// TTS model now runs in the single audio.cpp container. Stored configs still point at the old
    /// native ports, so this repoints them. Pure data migration — the schema does not change.
    /// </summary>
    /// <remarks>
    /// Literals, not enum names or settings constants: a migration is frozen at the moment it was
    /// written, and must keep doing the same thing if those names drift later.
    /// </remarks>
    public partial class RetireNativeTtsServices : Migration
    {
        private const string AudioCppBaseUrl = "http://localhost:8004";

        // The retired native services, as the watchdog registry knew them: chatterbox 8000,
        // voxcpm2 8003, qwen3-tts (design) 8100, qwen3-tts-base 8101. Only these hosts are local;
        // any other host is someone's own server and is left alone.
        private static readonly string[] NativeBaseUrlLiterals =
            (from host in new[] { "localhost", "127.0.0.1" }
             from port in new[] { 8000, 8003, 8100, 8101 }
             select $"'http://{host}:{port}'").ToArray();

        // Type (stored as the enum name) -> the default audio.cpp model id, and the key the settings
        // record reads it from. Qwen3VoiceDesignSettings has no JSON names, so its forms store
        // PascalCase and the Blazor form reads it case-sensitively; every other record names it
        // "modelId".
        private static readonly (string Type, string ModelId, string ModelKey)[] ParagraphTtsModels =
        [
            ("VoxCpm2", "voxcpm2", "modelId"),
            ("Chatterbox", "chatterbox", "modelId"),
            ("Qwen3Base", "qwen3-base", "modelId"),
            ("Breeze", "breeze-q8", "modelId"),
        ];

        private static readonly (string Type, string ModelId, string ModelKey)[] VoiceDesignModels =
        [
            ("VoxCpm2", "voxcpm2", "modelId"),
            ("Qwen3", "qwen3-design", "ModelId"),
            ("Breeze", "breeze-design", "modelId"),
        ];

        // SettingsJson key casing depends on which form wrote the row (plain Serialize writes
        // PascalCase, Web options camelCase); the runtime reads either. Touch whichever is present.
        private static readonly string[] BaseUrlKeys = ["baseUrl", "BaseUrl"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Chatterbox Turbo is gone (ADR 0010, audiocpp-tts 08); value 2 stays reserved. EF stored
            // the enum name while the member existed; '2' is how the bare value would round-trip.
            // Children are deleted explicitly rather than trusting the FK cascade, which only fires
            // when the connection has foreign_keys on.
            const string isTurbo = "Type IN ('ChatterboxTurbo', '2')";
            const string turboIds = $"SELECT Id FROM ParagraphTtsServiceConfigs WHERE {isTurbo}";
            migrationBuilder.Sql($"UPDATE Settings SET ActiveParagraphTtsConfigId = NULL WHERE ActiveParagraphTtsConfigId IN ({turboIds});");
            migrationBuilder.Sql($"DELETE FROM TextSubstitutionSteps WHERE ParagraphTtsServiceConfigId IN ({turboIds});");
            migrationBuilder.Sql($"DELETE FROM ToSentenceCaseConfigs WHERE ParagraphTtsServiceConfigId IN ({turboIds});");
            migrationBuilder.Sql($"DELETE FROM ParagraphTtsServiceConfigs WHERE {isTurbo};");

            Repoint(migrationBuilder, "ParagraphTtsServiceConfigs", ParagraphTtsModels);
            Repoint(migrationBuilder, "VoiceDesignServiceConfigs", VoiceDesignModels);

            // audio.cpp's VoxCPM2 has no normalize/denoise (bt-07); the typed settings dropped them
            // in 04, so they are dead keys. Every VoxCPM2 row, remote ones included — no client
            // reads them any more. Only rows that carry one are rewritten.
            string[] retiredVoxCpm2Keys = ["normalize", "denoise", "Normalize", "Denoise"];
            var carriesOne = string.Join(" OR ", retiredVoxCpm2Keys.Select(k => $"{Extract(k, "json_type")} IS NOT NULL"));
            var removePaths = string.Join(", ", retiredVoxCpm2Keys.Select(k => $"'$.{k}'"));
            foreach (var table in new[] { "ParagraphTtsServiceConfigs", "VoiceDesignServiceConfigs" })
            {
                migrationBuilder.Sql(
                    $"UPDATE {table} SET SettingsJson = json_remove(SettingsJson, {removePaths}) " +
                    $"WHERE Type = 'VoxCpm2' AND ({carriesOne});");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible by design: the native services this would point back at no longer exist.
        }

        private static void Repoint(
            MigrationBuilder migrationBuilder,
            string table,
            (string Type, string ModelId, string ModelKey)[] models)
        {
            var onNativeService = string.Join(" OR ", BaseUrlKeys.Select(IsOnNativeService));

            // Model id first: once the URL moves, the row no longer says it was native. A row that
            // already names a model (either casing) keeps it.
            foreach (var (type, modelId, modelKey) in models)
            {
                migrationBuilder.Sql(
                    $"UPDATE {table} SET SettingsJson = json_set(SettingsJson, '$.{modelKey}', '{modelId}') " +
                    $"WHERE Type = '{type}' AND ({onNativeService}) " +
                    $"AND {Extract("modelId")} IS NULL AND {Extract("ModelId")} IS NULL;");
            }

            foreach (var key in BaseUrlKeys)
            {
                migrationBuilder.Sql(
                    $"UPDATE {table} SET SettingsJson = json_set(SettingsJson, '$.{key}', '{AudioCppBaseUrl}') " +
                    $"WHERE {IsOnNativeService(key)};");
            }
        }

        /// <summary>
        /// SQL predicate: the row's <paramref name="key"/> is a native service URL. Mirrors the watchdog
        /// registry's normalisation (case, trailing slash).
        /// </summary>
        private static string IsOnNativeService(string key) =>
            $"(lower(rtrim({Extract(key)}, '/')) IN ({string.Join(", ", NativeBaseUrlLiterals)}))";

        /// <summary>
        /// The value at <c>$.key</c>, or NULL when absent. The CASE keeps json_extract away from a
        /// malformed blob, which would otherwise abort the whole migration; such a row is skipped.
        /// </summary>
        private static string Extract(string key, string function = "json_extract") =>
            $"(CASE WHEN json_valid(SettingsJson) THEN {function}(SettingsJson, '$.{key}') END)";
    }
}
