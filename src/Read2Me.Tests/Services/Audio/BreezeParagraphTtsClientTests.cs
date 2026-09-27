using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// Breeze paragraph TTS end to end down to the wire: the Breeze client over the real audio.cpp
    /// client and gate, against an in-memory audio.cpp server.
    /// </summary>
    public class BreezeParagraphTtsClientTests
    {
        private static readonly byte[] RefAudio = [1, 2, 3, 4, 5];

        private static ParagraphTtsServiceConfig Config(BreezeParagraphTtsSettings? settings = null) => new()
        {
            Name = "breeze",
            Type = ParagraphTtsServiceType.Breeze,
            SettingsJson = JsonSerializer.Serialize(
                settings ?? BreezeParagraphTtsSettings.Recommended with { BaseUrl = "http://acpp:8004" }),
        };

        private sealed record Sut(
            BreezeParagraphTtsClient Client,
            FakeAudioCppHandler Handler,
            FakeAiServiceReporter Reporter,
            SingleHandlerHttpClientFactory Factory);

        private static Sut Build(FakeAudioCppHandler? handler = null, bool managed = false)
        {
            handler ??= new FakeAudioCppHandler { LoadedModel = "breeze-q8" };
            var factory = new SingleHandlerHttpClientFactory(handler);
            var reporter = new FakeAiServiceReporter { Managed = managed };
            var gate = new AudioCppGate(factory, new EventBroadcaster<AudioGenEvent>());
            var audioCpp = new AudioCppClient(
                factory, gate, reporter, new AudioCppRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]),
                NullLogger<AudioCppClient>.Instance);
            return new Sut(new BreezeParagraphTtsClient(audioCpp), handler, reporter, factory);
        }

        private static Task<Stream> Generate(Sut sut, string? instructions, ParagraphTtsServiceConfig? config = null,
            string? transcript = "the reference transcript") =>
            sut.Client.GenerateAsync("Hello there.", instructions, new MemoryStream(RefAudio),
                config ?? Config(), null, transcript);

        private static JsonElement Options(Sut sut) =>
            JsonDocument.Parse(sut.Handler.SpeechBodies.Single().ToJsonString()).RootElement.GetProperty("options");

        [Fact]
        public async Task Posts_the_speech_request_with_model_input_base64_reference_and_transcript()
        {
            var sut = Build();

            var wav = await Generate(sut, null);

            var body = sut.Handler.SpeechBodies.Single();
            Assert.Equal("breeze-q8", body["model"]!.GetValue<string>());
            Assert.Equal("Hello there.", body["input"]!.GetValue<string>());
            Assert.Equal("base64", body["voice_ref"]!["type"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(RefAudio), body["voice_ref"]!["data"]!.GetValue<string>());
            Assert.Equal("the reference transcript", body["reference_text"]!.GetValue<string>());
            Assert.Equal(FakeAudioCppHandler.Wav, ((MemoryStream)wav).ToArray());
            Assert.Contains(AudioCppClient.HttpClientName, sut.Factory.Names);
        }

        [Fact]
        public async Task An_instructed_item_sends_the_instruction_verbatim_with_the_instructed_guidance_scale()
        {
            var sut = Build();

            await Generate(sut, "angry, shouting");

            var options = Options(sut);
            Assert.Equal("angry, shouting", options.GetProperty("instruction").GetString());
            Assert.Equal("3", options.GetProperty("guidance_scale").GetString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_plain_item_omits_the_instruction_and_uses_the_plain_guidance_scale(string? instructions)
        {
            var sut = Build();

            await Generate(sut, instructions);

            var options = Options(sut);
            Assert.False(options.TryGetProperty("instruction", out _));
            Assert.Equal("1", options.GetProperty("guidance_scale").GetString());
        }

        [Fact]
        public async Task Every_option_is_a_string_formatted_invariantly()
        {
            var sut = Build();
            var config = Config(BreezeParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://acpp:8004",
                PlainGuidanceScale = 1.5,
                Temperature = 0.75,
                TopK = 40,
                TopP = 0.95,
            });

            await Generate(sut, null, config);

            var options = Options(sut);
            foreach (var option in options.EnumerateObject())
                Assert.Equal(JsonValueKind.String, option.Value.ValueKind);
            Assert.Equal("1.5", options.GetProperty("guidance_scale").GetString());
            Assert.Equal("0.75", options.GetProperty("temperature").GetString());
            Assert.Equal("40", options.GetProperty("top_k").GetString());
            Assert.Equal("0.95", options.GetProperty("top_p").GetString());
        }

        [Fact]
        public async Task A_pinned_seed_is_sent_on_every_request()
        {
            var sut = Build();
            var config = Config(BreezeParagraphTtsSettings.Recommended with { BaseUrl = "http://acpp:8004", Seed = 1234 });

            await Generate(sut, null, config);
            await Generate(sut, null, config);

            Assert.All(sut.Handler.SpeechBodies, b => Assert.Equal("1234", b["options"]!["seed"]!.GetValue<string>()));
        }

        [Fact]
        public async Task Without_a_pinned_seed_each_request_gets_a_fresh_random_seed()
        {
            // audio.cpp defaults to seed 0, and a WER retry resends identical arguments — a fixed seed
            // would repeat the very failure being retried.
            var sut = Build();

            for (var i = 0; i < 5; i++)
                await Generate(sut, null);

            var seeds = sut.Handler.SpeechBodies.Select(b => b["options"]!["seed"]!.GetValue<string>()).ToList();
            Assert.All(seeds, s => Assert.True(int.TryParse(s, out _)));
            Assert.True(seeds.Distinct().Count() > 1);
        }

        [Fact]
        public async Task A_blank_transcript_is_omitted()
        {
            var sut = Build();

            await Generate(sut, null, transcript: null);

            Assert.False(sut.Handler.SpeechBodies.Single().ContainsKey("reference_text"));
        }

        [Fact]
        public async Task The_model_id_setting_names_the_model()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-custom" });
            var config = Config(BreezeParagraphTtsSettings.Recommended with { BaseUrl = "http://acpp:8004", ModelId = "breeze-custom" });

            await Generate(sut, null, config);

            Assert.Equal("breeze-custom", sut.Handler.SpeechBodies.Single()["model"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_503_is_retried_and_then_succeeds()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", BusyResponses = 2 });

            await Generate(sut, null);

            Assert.Equal(3, sut.Handler.SpeechBodies.Count);
            Assert.Empty(sut.Reporter.Failures);
        }

        [Fact]
        public async Task A_503_that_outlasts_the_retries_throws_tts_busy_without_reporting_an_outage()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", BusyResponses = 99 }, managed: true);

            var ex = await Assert.ThrowsAsync<TtsBusyException>(() => Generate(sut, null));

            Assert.Equal(4, sut.Handler.SpeechBodies.Count); // first try + 3 retries
            Assert.Equal("breeze-q8", ex.ModelId);
            Assert.Empty(sut.Reporter.Failures);
        }

        [Fact]
        public async Task A_hard_failure_on_a_managed_service_reports_it_and_throws_unavailable()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", FailWith = HttpStatusCode.InternalServerError }, managed: true);

            await Assert.ThrowsAsync<AiServiceUnavailableException>(() => Generate(sut, null));

            Assert.Equal("http://acpp:8004", Assert.Single(sut.Reporter.Failures).BaseUrl);
        }

        [Fact]
        public async Task A_hard_failure_on_an_unmanaged_service_rethrows_the_original()
        {
            var sut = Build(new FakeAudioCppHandler { LoadedModel = "breeze-q8", FailWith = HttpStatusCode.InternalServerError });

            await Assert.ThrowsAsync<HttpRequestException>(() => Generate(sut, null));
        }

        [Fact]
        public async Task A_success_is_reported()
        {
            var sut = Build();

            await Generate(sut, null);

            Assert.Equal("http://acpp:8004", Assert.Single(sut.Reporter.Successes));
        }
    }
}
