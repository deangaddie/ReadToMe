using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Read2Me.Services.Events;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// Qwen3 VoiceDesign down to the wire: the Qwen3 design client over the real audio.cpp client and
    /// gate, against an in-memory audio.cpp server. No reference audio; the sample text is spoken and
    /// the design prompt is the instruction.
    /// </summary>
    public class Qwen3VoiceDesignClientTests
    {
        private static VoiceDesignServiceConfig Config(Qwen3VoiceDesignSettings? settings = null) => new()
        {
            Name = "qwen3-design",
            Type = VoiceDesignServiceType.Qwen3,
            // The config forms write this record with the plain serializer (PascalCase keys).
            SettingsJson = JsonSerializer.Serialize(
                settings ?? Qwen3VoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004" }),
        };

        private sealed record Sut(Qwen3VoiceDesignClient Client, FakeAudioCppHandler Handler);

        private static Sut Build(FakeAudioCppHandler? handler = null)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "qwen3-design" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, new FakeAiServiceReporter(),
                new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new Qwen3VoiceDesignClient(audioCpp), handler);
        }

        private static Task<Stream> Design(Sut sut, VoiceDesignServiceConfig? config = null, string? overrideJson = null) =>
            sut.Client.DesignVoiceAsync(config ?? Config(), "A gravelly old sea captain.", "The sample sentence.", overrideJson);

        private static Dictionary<string, string> Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement.GetProperty("options")
                .EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!);

        [Fact]
        public async Task Speaks_the_sample_text_under_the_prompt_as_instruction_with_no_reference()
        {
            var sut = Build();

            var wav = await Design(sut);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("qwen3-design", body["model"]!.GetValue<string>());
            Assert.Equal("The sample sentence.", body["input"]!.GetValue<string>());
            Assert.Equal("A gravelly old sea captain.", Options(sut)["instruction"]);
            Assert.False(body.ContainsKey("voice_ref"));
            Assert.False(body.ContainsKey("reference_text"));
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
        }

        [Fact]
        public async Task Unset_sampling_knobs_send_only_the_instruction_and_a_random_seed()
        {
            var sut = Build();

            await Design(sut);

            var options = Options(sut);
            Assert.Equal(["instruction", "seed"], options.Keys.Order());
            Assert.True(int.TryParse(options["seed"], out _));
        }

        [Fact]
        public async Task Maps_the_set_knobs_onto_audiocpp_option_names_as_strings()
        {
            var sut = Build();

            await Design(sut, Config(Qwen3VoiceDesignSettings.Recommended with
            {
                BaseUrl = "http://acpp:8004",
                Temperature = 0.7,
                TopP = 0.95,
                TopK = 40,
                RepetitionPenalty = 1.1,
                MaxNewTokens = 2048,
                Seed = 9,
            }));

            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["instruction"] = "A gravelly old sea captain.",
                    ["temperature"] = "0.7",
                    ["top_p"] = "0.95",
                    ["top_k"] = "40",
                    ["repetition_penalty"] = "1.1",
                    ["max_tokens"] = "2048",
                    ["seed"] = "9",
                },
                Options(sut));
        }

        [Theory]
        [InlineData("auto", "auto")]
        [InlineData("en", "english")]
        [InlineData("ja", "japanese")]
        public async Task Sends_the_language_top_level_as_the_name_audiocpp_knows(string stored, string sent)
        {
            var sut = Build();

            await Design(sut, Config(Qwen3VoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004", Language = stored }));

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal(sent, body["language"]!.GetValue<string>());
            Assert.False(Options(sut).ContainsKey("language"));
        }

        [Fact]
        public async Task Names_the_configured_model_entry()
        {
            var sut = Build();

            await Design(sut, Config(Qwen3VoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004", ModelId = "qwen3-design-bf16" }));

            Assert.Equal("qwen3-design-bf16", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_per_voice_override_replaces_the_config_defaults()
        {
            var sut = Build();

            await Design(sut, overrideJson: """{"temperature":0.5,"topK":20}""");

            Assert.Equal("0.5", Options(sut)["temperature"]);
            Assert.Equal("20", Options(sut)["top_k"]);
        }

        [Fact]
        public async Task A_503_that_outlasts_the_retries_throws_tts_busy()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", BusyResponses = 99 });

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Design(sut));

            Assert.Equal("qwen3-design", ex.ModelId);
        }
    }
}
