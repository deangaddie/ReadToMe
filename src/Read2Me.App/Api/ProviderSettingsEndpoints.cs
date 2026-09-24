using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Read2Me.Services;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Llm;
using Read2Me.Services.Text;

namespace Read2Me.App.Api
{
    /// <param name="BuiltIn">False for one of the config's own substitutions, which the catalog lists after the built-ins.</param>
    /// <param name="Options">The step's own switches, stored beside the config (<c>toSentenceCaseConfig</c>); null when it has none.</param>
    public sealed record TextStepDto(
        string StepId, string Label, string Description, bool BuiltIn, IReadOnlyList<SettingsFieldDto>? Options = null);

    /// <param name="Text">The stored override; null when the built-in <paramref name="Default"/> is in use.</param>
    public sealed record VoiceDesignSampleTextResponse(string? Text, string Default);
    public sealed record VoiceDesignSampleTextRequest(string? Text = null);

    public sealed record VoiceDesignTestRequest(string? Prompt = null);
    public sealed record VoiceDesignTestResponse(string AudioBase64, string ContentType);

    public sealed record TranscriptionTestResponse(string Transcript);

    public sealed record SimilarityTestRequest(string? Text1 = null, string? Text2 = null);
    public sealed record SimilarityTestResponse(double Score, double Threshold, bool Pass);

    /// <summary>
    /// The TTS / voice-design / transcription / similarity settings pages' endpoints beyond the
    /// generic config areas (Angular ticket 22): the text-processing step catalog, the voice-design
    /// sample text and a Test action per provider. A provider that cannot be reached, or answers
    /// badly, is 422 with its reason — never a 500.
    /// </summary>
    public static class ProviderSettingsEndpoints
    {
        private const string ToSentenceCaseStepId = "to-sentence-case";
        private static readonly TimeSpan VoiceDesignTestTimeout = TimeSpan.FromSeconds(60);

        private const long MaxTestAudioBytes = 50L * 1024 * 1024;
        private static readonly HashSet<string> TestAudioExtensions = [".wav", ".mp3", ".aac"];

        /// <summary>Labels and new-config defaults of Blazor's <c>ToSentenceCaseFormItem</c>; keys are <c>toSentenceCaseConfig</c>'s.</summary>
        private static readonly IReadOnlyList<SettingsFieldDto> ToSentenceCaseOptions =
        [
            new("paragraphEnabled", "Normalise all-caps paragraphs", "boolean", Default: true),
            new("wordEnabled", "De-shout long all-caps words", "boolean", Default: true),
            new("wordMinLength", "Minimum word length", "number", Min: 1, Step: 1, Default: 5),
        ];

        public static void MapProviderSettingsEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/paragraph-tts/{id:int}/text-steps", GetTextStepsAsync)
                .WithSummary("The text-processing steps a TTS config may enable (its enabledStepIds): the built-ins, then the config's own substitutions in order. Id 0 lists the built-ins for a config not yet saved.");

            endpoints.MapGet("/api/settings/voice-design/sample-text", GetSampleTextAsync)
                .WithSummary("The sentence voice design speaks: the stored override (null when none) and the built-in default.");
            endpoints.MapPut("/api/settings/voice-design/sample-text", SetSampleTextAsync)
                .WithSummary("Override the voice-design sample sentence. Null, blank or the default text clears the override.");

            endpoints.MapPost("/api/settings/voice-design/{id:int}/test", TestVoiceDesignAsync)
                .WithSummary("Design a voice from a prompt with one config, speaking the sample text; answers the audio as base64. 60 s timeout. 422 with the reason when the provider fails.");
            endpoints.MapPost("/api/settings/transcription/{id:int}/test", TestTranscriptionAsync)
                .DisableAntiforgery()
                .WithSummary("Transcribe an uploaded audio file (multipart field 'file': wav, mp3 or aac, at most 50 MB) with one config. 422 with the reason when the provider fails.");
            endpoints.MapPost("/api/settings/semantic-similarity/{id:int}/test", TestSimilarityAsync)
                .WithSummary("Score two texts with one config against its pass threshold. 422 with the reason when the provider fails.");
        }

        private static async Task<IResult> GetTextStepsAsync(
            int id, ParagraphTtsSettingsService settings, ITextProcessingStepCatalog catalog)
        {
            if (id != 0 && (await settings.GetAllConfigsAsync()).All(c => c.Id != id))
                return Results.NotFound();

            var builtIns = catalog.GetAll(0).Select(s => s.Id).ToHashSet();
            return Results.Ok(catalog.GetAll(id)
                .Select(s => new TextStepDto(s.Id, s.DisplayName, s.Description, builtIns.Contains(s.Id),
                    s.Id == ToSentenceCaseStepId ? ToSentenceCaseOptions : null))
                .ToList());
        }

        private static async Task<IResult> GetSampleTextAsync(VoiceDesignSettingsService settings) =>
            Results.Ok(new VoiceDesignSampleTextResponse(
                await settings.GetSampleTextAsync(), PromptTemplates.VoiceDesignSampleSentence));

        private static async Task<IResult> SetSampleTextAsync(
            VoiceDesignSampleTextRequest request, VoiceDesignSettingsService settings)
        {
            var isOverride = !string.IsNullOrWhiteSpace(request.Text)
                && request.Text != PromptTemplates.VoiceDesignSampleSentence;
            await settings.SetSampleTextAsync(isOverride ? request.Text : null);
            return await GetSampleTextAsync(settings);
        }

        private static async Task<IResult> TestVoiceDesignAsync(
            int id, VoiceDesignTestRequest request, VoiceDesignSettingsService settings,
            IVoiceDesignClientResolver clients, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.Problem("A prompt is required.", statusCode: StatusCodes.Status400BadRequest);
            if ((await settings.GetAllConfigsAsync()).FirstOrDefault(c => c.Id == id) is not { } config)
                return Results.NotFound();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(VoiceDesignTestTimeout);
            try
            {
                var sampleText = await settings.GetSampleTextAsync();
                await using var audio = await clients.Resolve(config.Type).DesignVoiceAsync(
                    config, request.Prompt,
                    string.IsNullOrWhiteSpace(sampleText) ? PromptTemplates.VoiceDesignSampleSentence : sampleText,
                    null, timeout.Token);
                using var buffer = new MemoryStream();
                await audio.CopyToAsync(buffer, timeout.Token);
                return Results.Ok(new VoiceDesignTestResponse(Convert.ToBase64String(buffer.ToArray()), "audio/wav"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ProviderFailed($"No answer within {VoiceDesignTestTimeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex)
            {
                return ProviderFailed(ex.Message);
            }
        }

        private static async Task<IResult> TestTranscriptionAsync(
            int id, HttpRequest request, TranscriptionSettingsService settings,
            ITranscriptionClientResolver clients, CancellationToken ct)
        {
            if ((await settings.GetAllConfigsAsync()).FirstOrDefault(c => c.Id == id) is not { } config)
                return Results.NotFound();

            // Kestrel's default body cap is 30 MB; the Blazor test upload allows 50 MB.
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = MaxTestAudioBytes;

            var (file, refusal) = await UploadedFile.ReadAsync(
                request, TestAudioExtensions, MaxTestAudioBytes, "Audio exceeds the 50 MB limit.", ct);
            if (file is null)
                return refusal!;

            try
            {
                await using var audio = file.File.OpenReadStream();
                return Results.Ok(new TranscriptionTestResponse(
                    await clients.Resolve(config.Type).TranscribeAsync(config, audio, file.FileName, ct)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ProviderFailed(ex.Message);
            }
        }

        private static async Task<IResult> TestSimilarityAsync(
            int id, SimilarityTestRequest request, SemanticSimilaritySettingsService settings,
            ISemanticSimilarityClientResolver clients, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Text1) || string.IsNullOrWhiteSpace(request.Text2))
                return Results.Problem("Both texts are required.", statusCode: StatusCodes.Status400BadRequest);
            if ((await settings.GetAllConfigsAsync()).FirstOrDefault(c => c.Id == id) is not { } config)
                return Results.NotFound();

            try
            {
                var score = await clients.Resolve(config.Type).ComputeAsync(config, request.Text1, request.Text2, ct);
                var threshold = ProviderSettingsJson.ReadSimilarity(config).PassThreshold;
                return Results.Ok(new SimilarityTestResponse(score, threshold, score >= threshold));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ProviderFailed(ex.Message);
            }
        }

        private static IResult ProviderFailed(string reason) =>
            Results.Problem(reason, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
}
