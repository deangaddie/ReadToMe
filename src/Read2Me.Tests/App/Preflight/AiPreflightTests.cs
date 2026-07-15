using MudBlazor;
using NSubstitute;
using Read2Me.App.Services.Preflight;
using Read2Me.App.Shared;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Preflight
{
    public class AiPreflightTests
    {
        private sealed class StubResolver(params string[] urls) : IAiTaskRequirementsResolver
        {
            public Task<IReadOnlyList<string>> GetRequiredBaseUrlsAsync(AiTaskKind task, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<string>>(urls);
        }

        private static readonly DockerAiServiceRegistry Registry = new();

        private static FakeAiServiceControl ControlFor(params string[] serviceNames)
        {
            var control = new FakeAiServiceControl();
            foreach (var name in serviceNames)
            {
                var service = Registry.GetByName(name);
                control.ResolveByUrl[service.BaseUrl] = service;
            }
            return control;
        }

        private static AiPreflight Create(
            IAiTaskRequirementsResolver resolver, FakeAiServiceControl control, IDialogService? dialogs = null) =>
            new(resolver, control, Registry, dialogs ?? Substitute.For<IDialogService>());

        private static IDialogService DialogsReturning(DialogResult? result)
        {
            var reference = Substitute.For<IDialogReference>();
            reference.Result.Returns(Task.FromResult(result));
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ShowAsync<AiPreflightDialog>(
                    Arg.Any<string?>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
                .Returns(reference);
            return dialogs;
        }

        [Fact]
        public async Task AllRequiredReady_ReturnsTrue_WithoutDialog()
        {
            var control = ControlFor("llama");
            control.StatusByName["llama"] = AiServiceStatus.Ready;
            var dialogs = Substitute.For<IDialogService>();

            var ok = await Create(new StubResolver("http://localhost:8080"), control, dialogs)
                .EnsureReadyAsync(AiTaskKind.CharacterAttribution);

            Assert.True(ok);
            await dialogs.DidNotReceiveWithAnyArgs()
                .ShowAsync<AiPreflightDialog>(default, default(DialogParameters)!, default);
        }

        [Fact]
        public async Task OnlyUnmanagedEndpoints_ReturnsTrue_WithoutDialog()
        {
            var control = new FakeAiServiceControl { ResolveResult = null };
            var dialogs = Substitute.For<IDialogService>();

            var ok = await Create(new StubResolver("https://api.example.com"), control, dialogs)
                .EnsureReadyAsync(AiTaskKind.CharacterAttribution);

            Assert.True(ok);
            Assert.Equal(0, control.StatusCalls);
            await dialogs.DidNotReceiveWithAnyArgs()
                .ShowAsync<AiPreflightDialog>(default, default(DialogParameters)!, default);
        }

        [Fact]
        public async Task NoRequiredUrls_ReturnsTrue_WithoutDialog()
        {
            var control = new FakeAiServiceControl();
            var dialogs = Substitute.For<IDialogService>();

            var ok = await Create(new StubResolver(), control, dialogs)
                .EnsureReadyAsync(AiTaskKind.Transcription);

            Assert.True(ok);
            await dialogs.DidNotReceiveWithAnyArgs()
                .ShowAsync<AiPreflightDialog>(default, default(DialogParameters)!, default);
        }

        [Fact]
        public async Task StoppedRequiredService_DialogOk_ReturnsTrue()
        {
            var control = ControlFor("llama");
            control.StatusByName["llama"] = AiServiceStatus.Stopped;
            var dialogs = DialogsReturning(DialogResult.Ok(true));

            var ok = await Create(new StubResolver("http://localhost:8080"), control, dialogs)
                .EnsureReadyAsync(AiTaskKind.CharacterAttribution);

            Assert.True(ok);
            await dialogs.ReceivedWithAnyArgs(1)
                .ShowAsync<AiPreflightDialog>(default, default(DialogParameters)!, default);
        }

        [Fact]
        public async Task StoppedRequiredService_DialogCancelled_ReturnsFalse()
        {
            var control = ControlFor("llama");
            control.StatusByName["llama"] = AiServiceStatus.Stopped;
            var dialogs = DialogsReturning(DialogResult.Cancel());

            var ok = await Create(new StubResolver("http://localhost:8080"), control, dialogs)
                .EnsureReadyAsync(AiTaskKind.CharacterAttribution);

            Assert.False(ok);
        }

        [Fact]
        public async Task BuildPlan_StoppedCpuWhisper_DoesNotListRunningGpuServicesAsConflicts()
        {
            // Whisper is CPU-only, so it can start while llama (GPU) is running.
            var control = ControlFor("whisper");
            control.StatusResult = AiServiceStatus.Stopped;
            control.StatusByName["llama"] = AiServiceStatus.Ready;
            control.StatusByName["minilm-l6"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:9000"), control)
                .BuildPlanAsync(AiTaskKind.Transcription, CancellationToken.None);

            Assert.Equal(["whisper"], plan.ToStart.Select(i => i.Service.Name));
            Assert.Equal(AiServiceStatus.Stopped, plan.ToStart[0].Status);
            Assert.Empty(plan.Conflicts);
        }

        [Fact]
        public async Task BuildPlan_RequiredGpuService_NotListedAsConflict()
        {
            // chatterbox required + Starting (so it lands in ToStart), llama running elsewhere.
            var control = ControlFor("chatterbox");
            control.StatusResult = AiServiceStatus.Stopped;
            control.StatusByName["chatterbox"] = AiServiceStatus.Starting;
            control.StatusByName["llama"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:8000"), control)
                .BuildPlanAsync(AiTaskKind.AudioGeneration, CancellationToken.None);

            Assert.Equal(["chatterbox"], plan.ToStart.Select(i => i.Service.Name));
            Assert.Equal(["llama"], plan.Conflicts.Select(s => s.Name));
            Assert.DoesNotContain(plan.Conflicts, s => s.Name == "chatterbox");
        }

        [Fact]
        public async Task BuildPlan_RequiredReady_NoRivalGpu_NothingToDo()
        {
            var control = ControlFor("llama");
            control.StatusByName["llama"] = AiServiceStatus.Ready;
            // No other GPU container up (default Stopped), so the sweep finds nothing.

            var plan = await Create(new StubResolver("http://localhost:8080"), control)
                .BuildPlanAsync(AiTaskKind.CharacterAttribution, CancellationToken.None);

            Assert.True(plan.NothingToDo);
            Assert.Empty(plan.Conflicts);
        }

        [Fact]
        public async Task BuildPlan_RequiredReady_RivalGpuRunning_StopsIt()
        {
            // llama required and already Ready, but chatterbox (GPU) is still up holding VRAM.
            // The rival must be swept even though nothing needs starting.
            var control = ControlFor("llama");
            control.StatusByName["llama"] = AiServiceStatus.Ready;
            control.StatusByName["chatterbox"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:8080"), control)
                .BuildPlanAsync(AiTaskKind.CharacterAttribution, CancellationToken.None);

            Assert.False(plan.NothingToDo);
            Assert.Empty(plan.ToStart);
            Assert.Equal(["chatterbox"], plan.Conflicts.Select(s => s.Name));
        }

        [Fact]
        public async Task BuildPlan_VoiceDesignAudio_TtsReadyButLlamaUp_StopsLlama()
        {
            // Repro of the batch "generate audio for all characters" bug: qwen3-tts already answers
            // /docs (Ready) while a leftover llama holds the GPU. Pre-flight must still stop llama.
            var control = ControlFor("qwen3-tts");
            control.StatusByName["qwen3-tts"] = AiServiceStatus.Ready;
            control.StatusByName["llama"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:8100"), control)
                .BuildPlanAsync(AiTaskKind.VoiceDesignAudio, CancellationToken.None);

            Assert.False(plan.NothingToDo);
            Assert.Empty(plan.ToStart);
            Assert.Equal(["llama"], plan.Conflicts.Select(s => s.Name));
        }

        [Fact]
        public async Task BuildPlan_DuplicateResolvedServices_Deduplicated()
        {
            var control = ControlFor("llama");
            var llama = Registry.GetByName("llama");
            control.ResolveByUrl["http://127.0.0.1:8080"] = llama;
            control.StatusByName["llama"] = AiServiceStatus.Stopped;

            var plan = await Create(
                    new StubResolver("http://localhost:8080", "http://127.0.0.1:8080"), control)
                .BuildPlanAsync(AiTaskKind.CharacterAttribution, CancellationToken.None);

            Assert.Equal(["llama"], plan.ToStart.Select(i => i.Service.Name));
        }
    }
}
