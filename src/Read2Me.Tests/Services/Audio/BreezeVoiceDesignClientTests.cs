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
    /// Breeze voice design down to the wire: the Breeze design client over the real audio.cpp
    /// client and gate, against an in-memory audio.cpp server. Task <c>tts</c> — no reference audio,
    /// the DesignPrompt is the instruction and the sample text is what gets spoken.
    /// </summary>
    public class BreezeVoiceDesignClientTests
    {
        private static VoiceDesignServiceConfig Config(BreezeVoiceDesignSettings? settings = null) => new()
        {
            Name = "breeze-design",
            Type = VoiceDesignServiceType.Breeze,
            SettingsJson = JsonSerializer.Serialize(
                settings ?? BreezeVoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004" }),
        };

        private sealed record Sut(BreezeVoiceDesignClient Client, FakeAudioCppHandler Handler);

        private static Sut Build(FakeAudioCppHandler? handler = null)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "breeze-design" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, new FakeAiServiceReporter(),
                new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new BreezeVoiceDesignClient(audioCpp), handler);
        }

        private static Task<Stream> Design(Sut sut, VoiceDesignServiceConfig? config = null, string? overrideJson = null) =>
            sut.Client.DesignVoiceAsync(config ?? Config(), "A gravelly old sea captain.", "The sample sentence.", overrideJson);

        private static JsonElement Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement.GetProperty("options");

        [Fact]
        public async Task Speaks_the_sample_text_with_the_design_prompt_as_the_instruction_and_no_reference()
        {
            var sut = Build();

            var wav = await Design(sut);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("breeze-design", body["model"]!.GetValue<string>());
            Assert.Equal("The sample sentence.", body["input"]!.GetValue<string>());
            Assert.Equal("A gravelly old sea captain.", Options(sut).GetProperty("instruction").GetString());
            Assert.False(body.ContainsKey("voice_ref"));
            Assert.False(body.ContainsKey("reference_text"));
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
        }

        [Fact]
        public async Task Sends_the_guidance_scale_as_a_string()
        {
            var sut = Build();

            await Design(sut);

            Assert.Equal("3", Options(sut).GetProperty("guidance_scale").GetString());
        }

        [Fact]
        public async Task A_pinned_seed_is_sent_as_is()
        {
            var sut = Build();

            await Design(sut, Config(BreezeVoiceDesignSettings.Recommended with { BaseUrl = "http://acpp:8004", Seed = 42 }));

            Assert.Equal("42", Options(sut).GetProperty("seed").GetString());
        }

        [Fact]
        public async Task An_unpinned_seed_is_drawn_per_request()
        {
            var sut = Build();

            await Design(sut);

            Assert.True(int.TryParse(Options(sut).GetProperty("seed").GetString(), out _));
        }

        [Fact]
        public async Task A_per_voice_override_replaces_the_config_defaults()
        {
            var sut = Build();

            await Design(sut, overrideJson: """{"guidanceScale":2.5,"modelId":"breeze-design-alt"}""");

            Assert.Equal("breeze-design-alt", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
            Assert.Equal("2.5", Options(sut).GetProperty("guidance_scale").GetString());
        }

        [Fact]
        public async Task Goes_through_the_tts_gate_before_speaking()
        {
            var sut = Build();

            await Design(sut);

            Assert.Equal(["GET /v1/models", "POST /v1/audio/speech"], sut.Handler.Paths);
        }

        [Fact]
        public async Task A_503_that_outlasts_the_retries_throws_tts_busy()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", BusyResponses = 99 });

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Design(sut));

            Assert.Equal("breeze-design", ex.ModelId);
        }
    }
}
