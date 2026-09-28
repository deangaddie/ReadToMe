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
    /// Chatterbox paragraph TTS down to the wire: the Chatterbox client over the real audio.cpp
    /// client and gate, against an in-memory audio.cpp server. A plain clone — reference audio, no
    /// transcript, no instruction channel — with every knob sent explicitly.
    /// </summary>
    public class ChatterboxParagraphTtsClientTests
    {
        private static readonly byte[] RefAudio = [1, 2, 3, 4, 5];

        private static ParagraphTtsServiceConfig Config(ChatterboxParagraphTtsSettings? settings = null) => new()
        {
            Name = "chatterbox",
            Type = ParagraphTtsServiceType.Chatterbox,
            SettingsJson = JsonSerializer.Serialize(
                settings ?? ChatterboxParagraphTtsSettings.Recommended with { BaseUrl = "http://acpp:8004" }),
        };

        private sealed record Sut(ChatterboxParagraphTtsClient Client, FakeAudioCppHandler Handler);

        private static Sut Build(FakeAudioCppHandler? handler = null)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "chatterbox" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, new FakeAiServiceReporter(),
                new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new ChatterboxParagraphTtsClient(audioCpp), handler);
        }

        private static Task<Stream> Generate(Sut sut, string? instructions = null,
            ParagraphTtsServiceConfig? config = null, string? overrideJson = null) =>
            sut.Client.GenerateAsync("Hello there.", instructions, new MemoryStream(RefAudio),
                config ?? Config(), overrideJson, "the reference transcript");

        private static JsonElement Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement.GetProperty("options");

        [Fact]
        public async Task Posts_a_clone_with_base64_reference_and_no_transcript()
        {
            var sut = Build();

            var wav = await Generate(sut);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("chatterbox", body["model"]!.GetValue<string>());
            Assert.Equal("Hello there.", body["input"]!.GetValue<string>());
            Assert.Equal("base64", body["voice_ref"]!["type"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(RefAudio), body["voice_ref"]!["data"]!.GetValue<string>());
            Assert.False(body.ContainsKey("reference_text"));
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
        }

        [Fact]
        public async Task Maps_every_knob_onto_audiocpp_option_names_as_invariant_strings()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "chatterbox-f16" });
            var config = Config(ChatterboxParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://acpp:8004",
                ModelId = "chatterbox-f16",
                Exaggeration = 0.7,
                CfgWeight = 0.3,
                Temperature = 0.65,
                MinP = 0.1,
                TopP = 0.95,
                RepetitionPenalty = 1.35,
                Seed = 77,
            });

            await Generate(sut, config: config);

            Assert.Equal("chatterbox-f16", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
            var options = Options(sut);
            foreach (var option in options.EnumerateObject())
                Assert.Equal(JsonValueKind.String, option.Value.ValueKind);
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["exaggeration"] = "0.7",
                    ["guidance_scale"] = "0.3",
                    ["temperature"] = "0.65",
                    ["min_p"] = "0.1",
                    ["top_p"] = "0.95",
                    ["repetition_penalty"] = "1.35",
                    ["seed"] = "77",
                },
                options.EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!));
        }

        [Fact]
        public async Task The_recommended_settings_send_every_app_default_explicitly()
        {
            // audio.cpp's documented Chatterbox defaults differ from its code (bt-07), so nothing is left to it.
            var sut = Build();

            await Generate(sut);

            var options = Options(sut);
            // audio.cpp reads the input language only from the top level; an options.language never reaches the model.
            Assert.Equal("en", sut.Handler.SpeechBodies.Single()["language"]!.GetValue<string>());
            Assert.False(options.TryGetProperty("language", out _));
            Assert.Equal("0.5", options.GetProperty("exaggeration").GetString());
            Assert.Equal("0.5", options.GetProperty("guidance_scale").GetString());
            Assert.Equal("0.8", options.GetProperty("temperature").GetString());
            Assert.Equal("0.05", options.GetProperty("min_p").GetString());
            Assert.Equal("1", options.GetProperty("top_p").GetString());
            Assert.Equal("1.2", options.GetProperty("repetition_penalty").GetString());
            Assert.True(int.TryParse(options.GetProperty("seed").GetString(), out _));
            Assert.Equal("chatterbox", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
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
        public async Task Voice_instructions_and_the_reference_transcript_are_ignored()
        {
            var sut = Build();

            await Generate(sut, "angry, shouting");

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("Hello there.", body["input"]!.GetValue<string>());
            Assert.False(body.ContainsKey("reference_text"));
            Assert.False(Options(sut).TryGetProperty("instruction", out _));
        }

        [Fact]
        public async Task A_per_voice_override_replaces_the_config_defaults()
        {
            var sut = Build();

            await Generate(sut, overrideJson: """{"exaggeration":1.1,"cfg_weight":0.25}""");

            Assert.Equal("1.1", Options(sut).GetProperty("exaggeration").GetString());
            Assert.Equal("0.25", Options(sut).GetProperty("guidance_scale").GetString());
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
            // Another model is loaded and generating, so audio.cpp refuses the switch with 503 server_busy.
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "voxcpm2", BusyResponses = 99 });

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Generate(sut));

            Assert.Equal("chatterbox", ex.ModelId);
        }
    }
}
