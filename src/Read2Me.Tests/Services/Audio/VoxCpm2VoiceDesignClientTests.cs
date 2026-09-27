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
    /// VoxCPM2 voice design down to the wire: the VoxCPM2 design client over the real audio.cpp
    /// client and gate, against an in-memory audio.cpp server. No reference audio; the design
    /// prompt is the control, spoken as <c>(prompt)sampleText</c>.
    /// </summary>
    public class VoxCpm2VoiceDesignClientTests
    {
        private static VoiceDesignServiceConfig Config(VoxCpm2VoiceDesignSettings? settings = null) => new()
        {
            Name = "voxcpm2-design",
            Type = VoiceDesignServiceType.VoxCpm2,
            SettingsJson = JsonSerializer.Serialize(
                settings ?? VoxCpm2VoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };

        private sealed record Sut(VoxCpm2VoiceDesignClient Client, FakeAudioCppHandler Handler);

        private static Sut Build(FakeAudioCppHandler? handler = null)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "voxcpm2" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, new FakeAiServiceReporter(),
                new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new VoxCpm2VoiceDesignClient(audioCpp), handler);
        }

        private static Task<Stream> Design(Sut sut, VoiceDesignServiceConfig? config = null, string? overrideJson = null) =>
            sut.Client.DesignVoiceAsync(config ?? Config(), "A gravelly old sea captain.", "The sample sentence.", overrideJson);

        private static JsonElement Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement.GetProperty("options");

        [Fact]
        public async Task Speaks_the_sample_text_under_the_prompt_as_control_with_no_reference()
        {
            var sut = Build();

            var wav = await Design(sut);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("voxcpm2", body["model"]!.GetValue<string>());
            Assert.Equal("(A gravelly old sea captain.)The sample sentence.", body["input"]!.GetValue<string>());
            Assert.False(body.ContainsKey("voice_ref"));
            Assert.False(body.ContainsKey("reference_text"));
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
        }

        [Fact]
        public async Task Maps_the_settings_onto_audiocpp_option_names_as_strings()
        {
            var sut = Build();

            await Design(sut, Config(VoxCpm2VoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004", Seed = 9 }));

            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["guidance_scale"] = "2",
                    ["num_inference_steps"] = "10",
                    ["min_tokens"] = "2",
                    ["max_tokens"] = "4096",
                    ["retry_badcase"] = "true",
                    ["retry_badcase_max_times"] = "3",
                    ["retry_badcase_ratio_threshold"] = "6",
                    ["seed"] = "9",
                },
                Options(sut).EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!));
        }

        [Fact]
        public async Task A_per_voice_override_replaces_the_config_defaults()
        {
            var sut = Build();

            await Design(sut, overrideJson: """{"cfg_value":3.0,"inference_timesteps":20}""");

            Assert.Equal("3", Options(sut).GetProperty("guidance_scale").GetString());
            Assert.Equal("20", Options(sut).GetProperty("num_inference_steps").GetString());
        }

        [Fact]
        public async Task A_config_written_with_plain_serializer_options_still_finds_its_base_url()
        {
            // The Blazor form writes "baseUrl" (web defaults); a plain serialize writes "BaseUrl".
            var sut = Build();
            var config = Config();
            config.SettingsJson = JsonSerializer.Serialize(
                VoxCpm2VoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004" });

            await Design(sut, config);

            Assert.Single(sut.Handler.SpeechBodies);
        }

        [Fact]
        public async Task A_503_that_outlasts_the_retries_throws_tts_busy()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", BusyResponses = 99 });

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Design(sut));

            Assert.Equal("voxcpm2", ex.ModelId);
        }
    }
}
