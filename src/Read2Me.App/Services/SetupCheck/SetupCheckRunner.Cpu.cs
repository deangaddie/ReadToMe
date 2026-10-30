using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Api;
using Read2Me.Services;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Full check's CPU services, whisper then similarity: whisper transcribes the bundled
    /// sample clip, and the similarity service scores that transcript against the clip's text. Each
    /// stands alone: with no transcript, the clip's text is scored against itself.
    /// </summary>
    public sealed partial class SetupCheckRunner
    {
        private const string TranscriptionExpected = "The bundled sample clip transcribes to non-empty text";

        private IEnumerable<FullStep> CpuSteps(FullContext context)
        {
            if (context.Transcription is { } transcription)
                yield return new FullStep(
                    SetupCheckLane.Cpu, $"Transcribing the sample clip on {Where(context.Whisper, ServiceConfigBaseUrls.For(transcription))}",
                    RowIds(SetupCheckIds.Transcription, context.Whisper), Services(context.Whisper), 15,
                    () => FullTranscriptionAsync(context));
            if (context.Similarity is { } similarity)
                yield return new FullStep(
                    SetupCheckLane.Cpu, $"Scoring the transcript on {Where(context.SimilarityService, ServiceConfigBaseUrls.For(similarity))}",
                    RowIds(SetupCheckIds.Similarity, context.SimilarityService), Services(context.SimilarityService), 15,
                    () => FullSimilarityAsync(context));
        }

        private static string Where(DockerAiService? service, string? baseUrl) => service?.ContainerName ?? baseUrl ?? "its service";

        private static IReadOnlyList<string> RowIds(string configRow, DockerAiService? service) =>
            service is null ? [configRow] : [configRow, SetupCheckIds.Container(service.Name)];

        private static IReadOnlyList<string> Services(DockerAiService? service) => service is null ? [] : [service.Name];

        // ── Transcription ────────────────────────────────────────────────────

        private async Task<IReadOnlyList<SetupCheckRow>> FullTranscriptionAsync(FullContext context)
        {
            var config = context.Transcription!;
            var row = context.Row(SetupCheckIds.Transcription, SetupCheckGroup.AiConfigs, "Transcription")
                with { Tier = SetupCheckTier.Full, Expected = TranscriptionExpected };
            var url = $"{ServiceConfigBaseUrls.For(config)?.TrimEnd('/')}/inference";
            var settings = AiServicesCheck.TranscriptionSettings;

            return await RequestCheckAsync(context, context.Whisper, row, settings, url, async () =>
            {
                await using var clip = SampleClip.OpenWav();
                await using var scope = aiServices.Scopes.CreateAsyncScope();
                var transcript = (await scope.ServiceProvider.GetRequiredService<ITranscriptionClientResolver>()
                    .Resolve(config.Type).TranscribeAsync(config, clip, SampleClip.FileName)).Trim();
                if (transcript.Length == 0)
                    return new CheckAnswer(false, "Transcribed the sample clip to empty text", new SetupFixHint(
                        $"It answered with no words: check its model (ggml-base.en.bin in Infra/models/), or the config in {settings.Label}.",
                        settings, "Infra/models/"));
                context.Transcript = transcript;
                return new CheckAnswer(true, $"Transcribed the sample clip: \"{transcript}\"");
            });
        }

        // ── Similarity ───────────────────────────────────────────────────────

        private const string NoTranscript = "The transcription check gave no transcript, so the clip's text was scored against itself.";

        private async Task<IReadOnlyList<SetupCheckRow>> FullSimilarityAsync(FullContext context)
        {
            var config = context.Similarity!;
            var threshold = ProviderSettingsJson.ReadSimilarity(config).PassThreshold;
            var transcript = context.Transcript;
            var row = context.Row(SetupCheckIds.Similarity, SetupCheckGroup.AiConfigs, "Similarity");
            row = row with
            {
                Tier = SetupCheckTier.Full,
                Expected = $"The transcript scores at least the config's pass threshold ({Score(threshold)}) against the clip's text",
                Note = transcript is not null ? row.Note : row.Note is null ? NoTranscript : $"{row.Note}. {NoTranscript}",
            };
            var url = $"{ServiceConfigBaseUrls.For(config)?.TrimEnd('/')}/similarity";
            var settings = AiServicesCheck.SimilaritySettings;

            return await RequestCheckAsync(context, context.SimilarityService, row, settings, url, async () =>
            {
                await using var scope = aiServices.Scopes.CreateAsyncScope();
                var score = await scope.ServiceProvider.GetRequiredService<ISemanticSimilarityClientResolver>()
                    .Resolve(config.Type).ComputeAsync(config, SampleClip.Text, transcript ?? SampleClip.Text);
                return score >= threshold
                    ? new CheckAnswer(true, $"Scored {Score(score)} against the clip's text (pass threshold {Score(threshold)})")
                    : new CheckAnswer(false, $"Scored {Score(score)}, below the config's pass threshold {Score(threshold)}",
                        new SetupFixHint($"Check the config's model, or its pass threshold, in {settings.Label}.", settings));
            });
        }

        private static string Score(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        // ── Shared ───────────────────────────────────────────────────────────

        /// <summary>What a check's request found; <see cref="Fix"/> is where to go when it didn't pass.</summary>
        private sealed record CheckAnswer(bool Passed, string Found, SetupFixHint? Fix = null);

        /// <summary>
        /// <see cref="SendCheckAsync"/> for a check that answers one config row: that row's verdict, and
        /// the container's when managed.
        /// </summary>
        private async Task<IReadOnlyList<SetupCheckRow>> RequestCheckAsync(
            FullContext context, DockerAiService? service, SetupCheckRow row, SetupSettingsLink settings, string url,
            Func<Task<CheckAnswer>> request, string? model = null)
        {
            var (answer, containerRow) = await SendCheckAsync(context, service, settings, url, request, model);
            var verdict = row with
            {
                Result = answer.Passed ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                Found = answer.Found,
                Fix = answer.Fix ?? new SetupFixHint($"Change it in {settings.Label}.", settings),
            };
            return containerRow is null ? [verdict] : [verdict, containerRow];
        }

        /// <summary>
        /// Readies the container (when managed; a GPU one after the Preflight conflict sweep), sends the
        /// one request and answers what it found, with the container's row. A request the service
        /// doesn't answer at all fails both, and the container is left as it is for its logs; an HTTP
        /// error it answers, or an answer that fails the check (empty text, a low score), fails only the
        /// config's row, and the container is put back as usual. A model that won't load gets the hint
        /// that says where its GGUF should be.
        /// </summary>
        private async Task<(CheckAnswer Answer, SetupCheckRow? Container)> SendCheckAsync(
            FullContext context, DockerAiService? service, SetupSettingsLink settings, string url,
            Func<Task<CheckAnswer>> request, string? model)
        {
            SetupCheckRow? containerRow = null;
            ContainerUse? use = null;
            if (service is not null)
            {
                if (service.UsesGpu)
                    await SweepAsync(context, service);
                (containerRow, var ready, use) = await ReadyContainerAsync(context, service);
                if (!ready)
                    return (new CheckAnswer(false, $"Not sent: {service.ContainerName} isn't ready",
                        new SetupFixHint($"Fix the {service.ContainerName} row first, then run the Full check again.")), containerRow);
            }

            try
            {
                return (await request(), containerRow);
            }
            catch (Exception ex)
            {
                var cause = (ex as AiServiceUnavailableException)?.InnerException ?? ex;
                var found = $"POST {url} failed: {cause.Message}";
                var modelFix = ModelLoadFix(service, model, cause.Message);
                if (service is null)
                    return (new CheckAnswer(false, found, modelFix
                        ?? new SetupFixHint($"Check the service where it runs, or correct the config in {settings.Label}.", settings)), null);

                var logs = AiServicesCheck.LogsFix(service);
                var configFix = modelFix
                    ?? logs with { Text = $"{logs.Text.TrimEnd('.')}, or correct the config in {settings.Label}.", Link = settings };
                // The container answered with an error (a model that won't load, a busy server): the
                // fault is the config's, so the container is judged as it was and put back as usual.
                if (cause is HttpRequestException { StatusCode: not null } or TtsBusyException)
                    return (new CheckAnswer(false, found, configFix), containerRow);

                use!.Failed = true;
                use.Row = containerRow! with
                {
                    Result = SetupCheckResult.Failed,
                    Found = "Running, but didn't answer the check: left as it is for its logs",
                    Fix = logs,
                };
                return (new CheckAnswer(false, found, configFix), use.Row);
            }
        }
    }
}
