using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Shared.Voices;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.App.Api
{
    /// <summary>
    /// One step of the voice editor's checklist: what it is, the dials it exposes (same field shape
    /// as the provider settings schemas, so <c>r2m-settings-form</c> renders them) and the values
    /// those dials start at. Only dial keys appear in <see cref="Defaults"/>; a step's non-dial
    /// settings (silence-trim's output floor) are fixed server-side.
    /// </summary>
    public sealed record StepCatalogEntryDto(
        string StepId, string Label, string Blurb, IReadOnlyList<SettingsFieldDto> Dials, JsonElement Defaults);

    public sealed record PreviewStepRequest(string? StepId, JsonElement? Settings);
    public sealed record PreviewRequest(IReadOnlyList<PreviewStepRequest>? Steps);

    /// <summary><see cref="Applied"/> false means the step fell back to its input; <see cref="Reason"/> says why.</summary>
    public sealed record PreviewStageDto(string StepId, bool Applied, string? Reason, string Url);
    public sealed record PreviewResponse(string PreviewId, IReadOnlyList<PreviewStageDto> Stages);
    public sealed record ApplyPreviewRequest(string? PreviewId);

    /// <summary>
    /// The voice audio editor on the wire (Angular ticket 18). Apply names a <c>previewId</c> rather
    /// than sending bytes or a chain: the host, not the client, guarantees that what gets written is
    /// what was rendered and heard.
    /// </summary>
    public static class VoiceEditorEndpoints
    {
        public static void MapVoiceEditorEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/audio/steps/catalog", GetCatalog)
                .WithSummary("The post-process steps a scope offers, in chain order, each with its dials (settings-form fields) and defaults. Only scope=voice is served.");
            endpoints.MapPost("/api/projects/{folder}/voices/{voiceId:guid}/editor/preview", PreviewAsync)
                .WithSummary("Render a chain of steps over the voice's original audio. Steps run in catalog order whatever order they are sent; settings are dial values merged over the step's defaults. Answers a previewId (valid 30 minutes) and one playable stage per step.");
            endpoints.MapGet("/api/previews/{previewId}/{stepId}.wav", GetPreviewStage)
                .WithSummary("The audio after one stage of a rendered preview. 404 once the preview has expired.");
            endpoints.MapPost("/api/projects/{folder}/voices/{voiceId:guid}/editor/apply", ApplyAsync)
                .WithSummary("Write a rendered preview's final stage over the voice's audio, keeping the original for restore. 422 when the preview is unknown, expired or belongs to another voice.");
            endpoints.MapPost("/api/projects/{folder}/voices/{voiceId:guid}/editor/restore", RestoreAsync)
                .WithSummary("Put the voice's original audio back and forget the edit. A no-op for a voice that was never edited.");
            endpoints.MapGet("/api/projects/{folder}/voices/{voiceId:guid}/original.wav", GetOriginalAsync)
                .WithSummary("The voice's audio as it was before the editor touched it. 404 while the voice is unedited — the live WAV is the original then.");
        }

        private static IResult GetCatalog(string? scope)
        {
            if (!string.Equals(scope, "voice", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Only scope=voice has a step catalog.", statusCode: StatusCodes.Status400BadRequest);

            return Results.Ok(VoiceStepCatalog.Entries);
        }

        private static async Task<IResult> PreviewAsync(
            string folder, Guid voiceId, PreviewRequest? body, IFileSystem fs, ICharacterReader reader,
            IVoicePreviewRenderer renderer, IPreviewStore previews, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            if (voice is null)
                return Results.NotFound();

            if (body?.Steps is null || body.Steps.Count == 0)
                return Results.Problem("Tick at least one step to preview.", statusCode: StatusCodes.Status400BadRequest);

            var requested = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            foreach (var step in body.Steps)
            {
                if (step.StepId is null || !VoiceStepCatalog.Contains(step.StepId))
                    return Results.Problem($"'{step.StepId}' is not a voice editor step.", statusCode: StatusCodes.Status400BadRequest);
                if (!requested.TryAdd(step.StepId, step.Settings))
                    return Results.Problem($"Step '{step.StepId}' was sent twice.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (VoiceRefOrNull(folderId, voice) is not { } voiceRef)
                return NoAudio();

            var (chain, refusal) = VoiceStepCatalog.BuildChain(requested);
            if (refusal is not null)
                return Results.Problem(refusal, statusCode: StatusCodes.Status400BadRequest);

            // No tokens: the circuit-bound AudioPreviewStore never evicts, and this store keeps the bytes.
            var result = await renderer.RenderChainAsync(voiceRef, chain!, tokens: [], ct);
            if (result.Error is not null)
                return Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);

            var entry = previews.Save(folderId, voiceId, result.Steps);
            return Results.Ok(new PreviewResponse(
                entry.PreviewId,
                entry.Stages.Select(s => new PreviewStageDto(s.StepId, s.Applied, s.Reason, StageUrl(entry.PreviewId, s.StepId))).ToList()));
        }

        private static IResult GetPreviewStage(string previewId, string stepId, HttpResponse response, IPreviewStore previews)
        {
            var stage = previews.TryGet(previewId)?.Stages.FirstOrDefault(s => s.StepId == stepId);
            return stage is null ? Results.NotFound() : Wav(response, stage.Audio);
        }

        private static async Task<IResult> ApplyAsync(
            string folder, Guid voiceId, ApplyPreviewRequest? body, IFileSystem fs, ICharacterReader reader,
            IVoiceOriginalStore originals, IPreviewStore previews, IVoiceAudioEditor editor, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            if (voice is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(body?.PreviewId))
                return Results.Problem("previewId is required.", statusCode: StatusCodes.Status400BadRequest);

            var entry = previews.TryGet(body.PreviewId);
            if (entry is null || entry.Folder != folderId || entry.VoiceId != voiceId)
                return Results.Problem(
                    "That preview has expired or was not rendered for this voice — preview again before applying.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            if (VoiceRefOrNull(folderId, voice) is not { } voiceRef)
                return NoAudio();

            await editor.ApplyAsync(voiceRef, entry.Final, ct);
            return Results.Ok(VoiceEndpoints.ToDto(folderId, voice, originals));
        }

        private static async Task<IResult> RestoreAsync(
            string folder, Guid voiceId, IFileSystem fs, ICharacterReader reader,
            IVoiceOriginalStore originals, IVoiceAudioEditor editor, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            if (voice is null)
                return Results.NotFound();

            if (VoiceRefOrNull(folderId, voice) is not { } voiceRef)
                return NoAudio();

            await editor.RestoreOriginalAsync(voiceRef, ct);
            return Results.Ok(VoiceEndpoints.ToDto(folderId, voice, originals));
        }

        private static async Task<IResult> GetOriginalAsync(
            string folder, Guid voiceId, HttpResponse response, IFileSystem fs, ICharacterReader reader,
            IVoiceOriginalStore originals, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            if (voice is null)
                return Results.NotFound();

            var original = await originals.TryReadAsync(folderId, voice.CharacterId, voiceId, ct);
            return original is null ? Results.NotFound() : Wav(response, original);
        }

        private static VoiceAudioRef? VoiceRefOrNull(ProjectFolderId folderId, VoiceEntity voice) =>
            string.IsNullOrEmpty(voice.AudioFileName)
                ? null
                : new VoiceAudioRef(folderId, voice.CharacterId, voice.Id, voice.AudioFileName);

        private static IResult NoAudio() =>
            Results.Problem("This voice has no audio to edit.", statusCode: StatusCodes.Status422UnprocessableEntity);

        internal static string StageUrl(string previewId, string stepId) => $"/api/previews/{previewId}/{stepId}.wav";

        /// Previews are rewritten per render and the original vanishes on restore, so neither may be
        /// cached. Results.Bytes sets the explicit Content-Length the audio element needs for a duration.
        private static IResult Wav(HttpResponse response, byte[] audio)
        {
            response.Headers.CacheControl = "no-store";
            return Results.Bytes(audio, "audio/wav");
        }
    }

    /// <summary>
    /// The voice-scope step list as the editor shows it: labels and blurbs from <see cref="VoiceStepRow"/>
    /// (so Blazor and Angular say the same thing), dial ranges from the Blazor dials, defaults from
    /// <see cref="AudioPostProcessStepDefaults"/>. Chain order is the defaults' order — the client
    /// ticks steps, it never orders them.
    /// </summary>
    public static class VoiceStepCatalog
    {
        private static readonly IReadOnlyList<AudioPostProcessStepConfig> Defaults =
            AudioPostProcessStepDefaults.For(StepScope.Voice);

        public static readonly IReadOnlyList<StepCatalogEntryDto> Entries = Defaults.Select(Entry).ToList();

        public static bool Contains(string stepId) => Entries.Any(e => e.StepId == stepId);

        /// <summary>
        /// The configs to render for the requested steps, in catalog order. Each step's settings are its
        /// defaults with the request's dial values laid over — only dial keys are taken, so a client
        /// cannot reach a step's fixed settings, and each value must sit inside the dial's advertised
        /// range or option list (the Blazor slider made anything else unreachable; the API refuses it).
        /// </summary>
        /// <returns>The chain, or a refusal message for the 400.</returns>
        public static (IReadOnlyList<AudioPostProcessStepConfig>? Chain, string? Refusal) BuildChain(
            IReadOnlyDictionary<string, JsonElement?> requested)
        {
            var chain = new List<AudioPostProcessStepConfig>();
            foreach (var defaults in Defaults)
            {
                if (!requested.TryGetValue(defaults.StepId, out var settings))
                    continue;

                var merged = defaults.Settings is { } d ? JsonNode.Parse(d.GetRawText())!.AsObject() : new JsonObject();
                if (settings is { ValueKind: JsonValueKind.Object } sent)
                    foreach (var dial in DialsFor(defaults.StepId))
                    {
                        if (!sent.TryGetProperty(dial.Key, out var value))
                            continue;
                        if (Refuse(defaults.StepId, dial, value) is { } refusal)
                            return (null, refusal);
                        merged[dial.Key] = JsonNode.Parse(value.GetRawText());
                    }

                chain.Add(new AudioPostProcessStepConfig(
                    defaults.StepId, true, JsonSerializer.SerializeToElement(merged, AudioPostProcessJson.Options)));
            }

            return (chain, null);
        }

        private static string? Refuse(string stepId, SettingsFieldDto dial, JsonElement value)
        {
            switch (dial.Kind)
            {
                case "number":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n))
                        return $"{stepId}.{dial.Key} must be a number.";
                    if (n < dial.Min || n > dial.Max)
                        return $"{stepId}.{dial.Key} must be between {dial.Min} and {dial.Max}.";
                    return null;
                case "enum":
                    var option = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    return dial.Options!.Any(o => o.Value == option)
                        ? null
                        : $"{stepId}.{dial.Key} must be one of {string.Join(", ", dial.Options!.Select(o => o.Value))}.";
                default:
                    return null;
            }
        }

        private static StepCatalogEntryDto Entry(AudioPostProcessStepConfig defaults)
        {
            var row = VoiceStepRow.From(defaults);
            var dials = DialsFor(defaults.StepId);
            var values = new JsonObject();
            if (defaults.Settings is { } settings)
                foreach (var dial in dials)
                    if (settings.TryGetProperty(dial.Key, out var value))
                        values[dial.Key] = JsonNode.Parse(value.GetRawText());

            var defaultsElement = JsonSerializer.SerializeToElement(values);
            return new StepCatalogEntryDto(
                defaults.StepId, row.Label, row.Blurb,
                dials.Select(d => d with { Default = defaultsElement.TryGetProperty(d.Key, out var v) ? v : null }).ToList(),
                defaultsElement);
        }

        private static IReadOnlyList<SettingsFieldDto> DialsFor(string stepId) => stepId switch
        {
            AudioPostProcessStepIds.DePlosive =>
            [
                new("cutoffHz", "Cutoff (Hz)", "number", DePlosiveSettings.MinCutoffHz, DePlosiveSettings.MaxCutoffHz, 5,
                    Help: "Above ~100 Hz this starts eating a deep voice's fundamental."),
            ],
            AudioPostProcessStepIds.Denoise =>
            [
                new("strength", "Strength", "number", DenoiseSettings.MinStrength, DenoiseSettings.MaxStrength, 1),
            ],
            AudioPostProcessStepIds.HissReduce =>
            [
                new("preset", "Strength", "enum", Options:
                [
                    new(HissReducePresets.Light, "Light"),
                    new(HissReducePresets.Strong, "Strong"),
                ]),
            ],
            AudioPostProcessStepIds.ConsonantSoften =>
            [
                new("preset", "Preset", "enum", Options:
                [
                    new(ConsonantSoftenPresets.Light, "Light"),
                    new(ConsonantSoftenPresets.Medium, "Medium"),
                    new(ConsonantSoftenPresets.Strong, "Strong"),
                ]),
            ],
            AudioPostProcessStepIds.SilenceTrim =>
            [
                new("thresholdDb", "Threshold (dB)", "number",
                    SilenceTrimSettings.VoiceMinThresholdDb, SilenceTrimSettings.VoiceMaxThresholdDb, SilenceTrimSettings.VoiceThresholdStepDb),
                new("padMs", "Pad (ms)", "number",
                    SilenceTrimSettings.VoiceMinPadMs, SilenceTrimSettings.VoiceMaxPadMs, SilenceTrimSettings.VoicePadStepMs),
            ],
            _ => [],
        };
    }
}
