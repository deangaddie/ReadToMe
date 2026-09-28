using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Events;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// Qwen3-Base paragraph TTS down to the wire: the Qwen3 client over the real audio.cpp client
    /// and gate, against an in-memory audio.cpp server. An ICL clone — reference audio plus its
    /// transcript — with the sampling knobs sent only when set.
    /// </summary>
    public class Qwen3ParagraphTtsClientTests
    {
        private static readonly byte[] RefAudio = [1, 2, 3, 4, 5];

        private static ParagraphTtsServiceConfig Config(Qwen3ParagraphTtsSettings? settings = null) => new()
        {
            Name = "qwen3-base",
            Type = ParagraphTtsServiceType.Qwen3Base,
            SettingsJson = JsonSerializer.Serialize(
                settings ?? Qwen3ParagraphTtsSettings.Recommended with { BaseUrl = "http://acpp:8004" }),
        };

        private sealed record Sut(Qwen3ParagraphTtsClient Client, FakeAudioCppHandler Handler);

        private static Sut Build(FakeAudioCppHandler? handler = null)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "qwen3-base" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, new FakeAiServiceReporter(),
                new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new Qwen3ParagraphTtsClient(audioCpp), handler);
        }

        private static Task<Stream> Generate(Sut sut, string? instructions = null,
            ParagraphTtsServiceConfig? config = null, string? overrideJson = null,
            string? transcript = "the reference transcript") =>
            sut.Client.GenerateAsync("Hello there.", instructions, new MemoryStream(RefAudio),
                config ?? Config(), overrideJson, transcript);

        private static Dictionary<string, string> Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement
                .GetProperty("options").EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!);

        [Fact]
        public async Task Posts_an_icl_clone_with_base64_reference_and_the_voice_transcript()
        {
            var sut = Build();

            var wav = await Generate(sut);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("qwen3-base", body["model"]!.GetValue<string>());
            Assert.Equal("Hello there.", body["input"]!.GetValue<string>());
            Assert.Equal("base64", body["voice_ref"]!["type"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(RefAudio), body["voice_ref"]!["data"]!.GetValue<string>());
            Assert.Equal("the reference transcript", body["reference_text"]!.GetValue<string>());
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_missing_transcript_throws_before_any_request(string? transcript)
        {
            var sut = Build();

            await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(sut, transcript: transcript));

            Assert.Empty(sut.Handler.Paths);
        }

        [Theory]
        [InlineData("auto", "auto")]
        [InlineData("en", "english")]
        [InlineData("zh", "chinese")]
        [InlineData("ja", "japanese")]
        [InlineData("", "auto")]
        public async Task The_language_code_goes_top_level_as_the_name_audiocpp_knows(string code, string expected)
        {
            // audio.cpp reads the Qwen3 language from the top-level field only, by codec language name.
            var sut = Build();

            await Generate(sut, config: Config(Qwen3ParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://acpp:8004",
                Language = code,
            }));

            Assert.Equal(expected, sut.Handler.SpeechBodies.Single()["language"]!.GetValue<string>());
            Assert.False(Options(sut).ContainsKey("language"));
        }

        [Fact]
        public async Task The_recommended_settings_send_only_a_seed_leaving_sampling_to_audiocpp()
        {
            var sut = Build();

            await Generate(sut);

            var options = Options(sut);
            Assert.Equal(["seed"], options.Keys);
            Assert.True(int.TryParse(options["seed"], out _));
        }

        [Fact]
        public async Task Set_knobs_map_onto_audiocpp_option_names_as_invariant_strings()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "qwen3-base-bf16" });
            var config = Config(Qwen3ParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://acpp:8004",
                ModelId = "qwen3-base-bf16",
                Temperature = 0.7,
                TopP = 0.95,
                TopK = 40,
                RepetitionPenalty = 1.1,
                MaxNewTokens = 512,
                Seed = 77,
            });

            await Generate(sut, config: config);

            Assert.Equal("qwen3-base-bf16", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["temperature"] = "0.7",
                    ["top_p"] = "0.95",
                    ["top_k"] = "40",
                    ["repetition_penalty"] = "1.1",
                    ["max_tokens"] = "512",
                    ["seed"] = "77",
                },
                Options(sut));
        }

        [Fact]
        public async Task Without_a_pinned_seed_each_request_gets_a_fresh_random_seed()
        {
            var sut = Build();

            for (var i = 0; i < 5; i++)
                await Generate(sut);

            var seeds = sut.Handler.SpeechBodies.Select(b => b["options"]!["seed"]!.GetValue<string>()).ToList();
            Assert.True(seeds.Distinct().Count() > 1);
        }

        [Fact]
        public async Task A_per_voice_override_replaces_the_config_defaults()
        {
            var sut = Build();

            await Generate(sut, overrideJson: """{"temperature":0.55,"language":"fr"}""");

            Assert.Equal("0.55", Options(sut)["temperature"]);
            Assert.Equal("french", sut.Handler.SpeechBodies.Single()["language"]!.GetValue<string>());
        }

        [Fact]
        public async Task Voice_instructions_are_ignored()
        {
            var sut = Build();

            await Generate(sut, "angry, shouting");

            Assert.Equal("Hello there.", sut.Handler.SpeechBodies.Single()["input"]!.GetValue<string>());
            Assert.False(Options(sut).ContainsKey("instruction"));
        }

        [Fact]
        public async Task Goes_through_the_tts_gate_before_speaking()
        {
            var sut = Build();

            await Generate(sut);

            Assert.Equal(["GET /v1/models", "POST /v1/audio/speech"], sut.Handler.Paths);
        }

        [Fact]
        public async Task A_503_that_outlasts_the_retries_throws_tts_busy()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "voxcpm2", BusyResponses = 99 });

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Generate(sut));

            Assert.Equal("qwen3-base", ex.ModelId);
        }
    }
}
