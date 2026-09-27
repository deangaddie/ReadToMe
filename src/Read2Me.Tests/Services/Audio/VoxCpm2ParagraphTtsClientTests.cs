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
    /// VoxCPM2 paragraph TTS down to the wire: the VoxCPM2 client over the real audio.cpp client
    /// and gate, against an in-memory audio.cpp server. Controllable clone — reference audio but no
    /// transcript, and the instruction rides in the input as <c>(ctl)text</c>.
    /// </summary>
    public class VoxCpm2ParagraphTtsClientTests
    {
        private static readonly byte[] RefAudio = [1, 2, 3, 4, 5];

        private static ParagraphTtsServiceConfig Config(VoxCpm2ParagraphTtsSettings? settings = null) => new()
        {
            Name = "voxcpm2",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = JsonSerializer.Serialize(
                settings ?? VoxCpm2ParagraphTtsSettings.Recommended with { BaseUrl = "http://acpp:8004" }),
        };

        private sealed record Sut(VoxCpm2ParagraphTtsClient Client, FakeAudioCppHandler Handler);

        private static Sut Build(FakeAudioCppHandler? handler = null)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "voxcpm2" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, new FakeAiServiceReporter(),
                new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new VoxCpm2ParagraphTtsClient(audioCpp), handler);
        }

        private static Task<Stream> Generate(Sut sut, string? instructions,
            ParagraphTtsServiceConfig? config = null, string? overrideJson = null) =>
            sut.Client.GenerateAsync("Hello there.", instructions, new MemoryStream(RefAudio),
                config ?? Config(), overrideJson, "the reference transcript");

        private static JsonElement Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement.GetProperty("options");

        [Fact]
        public async Task Posts_a_controllable_clone_with_base64_reference_and_no_transcript()
        {
            var sut = Build();

            var wav = await Generate(sut, null);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("voxcpm2", body["model"]!.GetValue<string>());
            Assert.Equal("Hello there.", body["input"]!.GetValue<string>());
            Assert.Equal("base64", body["voice_ref"]!["type"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(RefAudio), body["voice_ref"]!["data"]!.GetValue<string>());
            // A transcript would switch VoxCPM2 to "ultimate" clone, which drops the control.
            Assert.False(body.ContainsKey("reference_text"));
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
        }

        [Fact]
        public async Task An_instructed_item_prefixes_the_input_with_the_control()
        {
            var sut = Build();

            await Generate(sut, " angry, shouting ");

            Assert.Equal("(angry, shouting)Hello there.", sut.Handler.SpeechBodies.Single()["input"]!.GetValue<string>());
            Assert.False(Options(sut).TryGetProperty("instruction", out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_plain_item_sends_the_text_unprefixed(string? instructions)
        {
            var sut = Build();

            await Generate(sut, instructions);

            Assert.Equal("Hello there.", sut.Handler.SpeechBodies.Single()["input"]!.GetValue<string>());
        }

        [Fact]
        public async Task Maps_the_settings_onto_audiocpp_option_names_as_invariant_strings()
        {
            var sut = Build();
            var config = Config(VoxCpm2ParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://acpp:8004",
                CfgValue = 2.5,
                InferenceTimesteps = 12,
                MinLen = 3,
                MaxLen = 2048,
                RetryBadcase = false,
                RetryBadcaseMaxTimes = 4,
                RetryBadcaseRatioThreshold = 5.5,
                Seed = 77,
            });

            await Generate(sut, null, config);

            var options = Options(sut);
            foreach (var option in options.EnumerateObject())
                Assert.Equal(JsonValueKind.String, option.Value.ValueKind);
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["guidance_scale"] = "2.5",
                    ["num_inference_steps"] = "12",
                    ["min_tokens"] = "3",
                    ["max_tokens"] = "2048",
                    ["retry_badcase"] = "false",
                    ["retry_badcase_max_times"] = "4",
                    ["retry_badcase_ratio_threshold"] = "5.5",
                    ["seed"] = "77",
                },
                options.EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!));
        }

        [Fact]
        public async Task The_recommended_settings_send_the_app_defaults()
        {
            var sut = Build();

            await Generate(sut, null);

            var options = Options(sut);
            Assert.Equal("2", options.GetProperty("guidance_scale").GetString());
            Assert.Equal("10", options.GetProperty("num_inference_steps").GetString());
            Assert.Equal("2", options.GetProperty("min_tokens").GetString());
            Assert.Equal("4096", options.GetProperty("max_tokens").GetString());
            Assert.Equal("true", options.GetProperty("retry_badcase").GetString());
            Assert.Equal("3", options.GetProperty("retry_badcase_max_times").GetString());
            Assert.Equal("6", options.GetProperty("retry_badcase_ratio_threshold").GetString());
            Assert.True(int.TryParse(options.GetProperty("seed").GetString(), out _));
        }

        [Fact]
        public async Task Without_a_pinned_seed_each_request_gets_a_fresh_random_seed()
        {
            var sut = Build();

            for (var i = 0; i < 5; i++)
                await Generate(sut, null);

            var seeds = sut.Handler.SpeechBodies.Select(b => b["options"]!["seed"]!.GetValue<string>()).ToList();
            Assert.True(seeds.Distinct().Count() > 1);
        }

        [Fact]
        public async Task A_per_voice_override_replaces_the_config_defaults()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "voxcpm2-alt" });

            await Generate(sut, null, overrideJson: """{"cfg_value":3.5,"modelId":"voxcpm2-alt"}""");

            Assert.Equal("voxcpm2-alt", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
            Assert.Equal("3.5", Options(sut).GetProperty("guidance_scale").GetString());
        }

        [Fact]
        public async Task Goes_through_the_tts_gate_before_speaking()
        {
            var sut = Build();

            await Generate(sut, null);

            Assert.Equal(["GET /v1/models", "POST /v1/audio/speech"], sut.Handler.Paths);
        }

        [Fact]
        public async Task A_503_that_outlasts_the_retries_throws_tts_busy()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", BusyResponses = 99 });

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Generate(sut, null));

            Assert.Equal("voxcpm2", ex.ModelId);
        }
    }
}
