using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Text;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class TextPreprocessingTtsClientTests
    {
        // Records the text (and other args) passed to it; returns a dummy stream.
        private sealed class CapturingInnerClient : IParagraphTtsClient
        {
            public string? CapturedText;
            public string? CapturedVoiceInstructions;
            public byte[]? CapturedReferenceAudio;
            public string? CapturedSettingsOverrideJson;

            public async Task<Stream> GenerateAsync(
                string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig config, string? settingsOverrideJson,
                CancellationToken ct = default)
            {
                CapturedText = text;
                CapturedVoiceInstructions = voiceInstructions;
                using var ms = new MemoryStream();
                await referenceAudioStream.CopyToAsync(ms, ct);
                CapturedReferenceAudio = ms.ToArray();
                CapturedSettingsOverrideJson = settingsOverrideJson;
                return new MemoryStream(new byte[] { 1, 2, 3 });
            }
        }

        private sealed class UppercaseStep : ITextProcessingStep
        {
            public string Process(string text) => text.ToUpperInvariant();
        }

        private sealed class AppendBangStep : ITextProcessingStep
        {
            public string Process(string text) => text + "!";
        }

        private sealed class NullSubstitutionStepSource : ITextSubstitutionStepSource
        {
            public ITextProcessingStep? Resolve(string stepId) => null;
        }

        private static TextPreprocessingTtsClient Build(
            IParagraphTtsClient inner,
            Action<ServiceCollection>? configure = null)
        {
            var sc = new ServiceCollection();
            sc.AddScoped<ITextSubstitutionStepSource, NullSubstitutionStepSource>();
            configure?.Invoke(sc);
            var sp = sc.BuildServiceProvider();
            return new TextPreprocessingTtsClient(inner, sp, NullLogger<TextPreprocessingTtsClient>.Instance);
        }

        private static ParagraphTtsServiceConfig ConfigWith(params string[] stepIds) =>
            new() { EnabledStepIds = [.. stepIds] };

        private static async Task<string> RunAndCapture(
            TextPreprocessingTtsClient client,
            CapturingInnerClient inner,
            string text,
            ParagraphTtsServiceConfig config)
        {
            using var refAudio = new MemoryStream(new byte[] { 9 });
            await client.GenerateAsync(text, null, refAudio, config, null);
            return inner.CapturedText!;
        }

        [Fact]
        public async Task EmptyEnabledList_InnerReceivesOriginalText()
        {
            var inner = new CapturingInnerClient();
            var client = Build(inner);

            var captured = await RunAndCapture(client, inner, "hello (world)", ConfigWith());

            Assert.Equal("hello (world)", captured);
        }

        [Fact]
        public async Task UnknownStepId_InnerStillCalled_NoThrow()
        {
            var inner = new CapturingInnerClient();
            var client = Build(inner);

            var captured = await RunAndCapture(client, inner, "hello (world)", ConfigWith("does-not-exist"));

            Assert.Equal("hello (world)", captured);
        }

        [Fact]
        public async Task TwoSteps_AppliedInOrder_UppercaseThenBang()
        {
            var inner = new CapturingInnerClient();
            var client = Build(inner, sc =>
            {
                sc.AddKeyedSingleton<ITextProcessingStep, UppercaseStep>("uppercase");
                sc.AddKeyedSingleton<ITextProcessingStep, AppendBangStep>("append-bang");
            });

            var captured = await RunAndCapture(client, inner, "hello", ConfigWith("uppercase", "append-bang"));

            Assert.Equal("HELLO!", captured);
        }

        [Fact]
        public async Task TwoSteps_ReversedOrder_BangThenUppercase()
        {
            var inner = new CapturingInnerClient();
            var client = Build(inner, sc =>
            {
                sc.AddKeyedSingleton<ITextProcessingStep, UppercaseStep>("uppercase");
                sc.AddKeyedSingleton<ITextProcessingStep, AppendBangStep>("append-bang");
            });

            var captured = await RunAndCapture(client, inner, "hello", ConfigWith("append-bang", "uppercase"));

            Assert.Equal("HELLO!", captured); // "hello" -> "hello!" -> "HELLO!"
        }

        [Fact]
        public async Task PassThrough_OtherArgs_ReachInnerUnchanged()
        {
            var inner = new CapturingInnerClient();
            var client = Build(inner);
            var refBytes = new byte[] { 7, 8, 9 };
            const string overrideJson = """{"cfg":1}""";
            const string voiceInstructions = "speak slowly";

            using var refAudio = new MemoryStream(refBytes);
            await client.GenerateAsync("text", voiceInstructions, refAudio, ConfigWith(), overrideJson);

            Assert.Equal(voiceInstructions, inner.CapturedVoiceInstructions);
            Assert.Equal(refBytes, inner.CapturedReferenceAudio);
            Assert.Equal(overrideJson, inner.CapturedSettingsOverrideJson);
        }

        [Fact]
        public async Task SubstitutionSource_Fallback_AppliesWhenKeyedDiMisses()
        {
            const string substitutionGuid = "a1b2c3d4-0000-0000-0000-000000000001";
            var inner = new CapturingInnerClient();

            var client = Build(inner, sc =>
            {
                sc.AddScoped<ITextSubstitutionStepSource>(_ =>
                    new FakeSubstitutionStepSource(substitutionGuid, new TextSubstitutionStepImpl("(", ",")));
            });

            var captured = await RunAndCapture(client, inner, "hello (world)", ConfigWith(substitutionGuid));

            Assert.Equal("hello ,world)", captured);
        }

        [Fact]
        public async Task UnknownId_NotInDiAndNotInSource_StillNoThrow()
        {
            var inner = new CapturingInnerClient();
            var client = Build(inner); // NullSubstitutionStepSource registered

            var captured = await RunAndCapture(client, inner, "hello", ConfigWith("completely-unknown-id"));

            Assert.Equal("hello", captured);
        }

        private sealed class FakeSubstitutionStepSource(string knownId, ITextProcessingStep step) : ITextSubstitutionStepSource
        {
            public ITextProcessingStep? Resolve(string stepId) => stepId == knownId ? step : null;
        }
    }
}
