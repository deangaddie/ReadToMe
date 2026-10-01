using Read2Me.App.Services.Preflight;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Preflight
{
    public class AiPreflightPlannerTests
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

        private static AiPreflightPlanner Create(IAiTaskRequirementsResolver resolver, FakeAiServiceControl control) =>
            new(resolver, control, Registry);

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
            // audiocpp required + Starting (so it lands in ToStart), llama running elsewhere.
            var control = ControlFor("audiocpp");
            control.StatusResult = AiServiceStatus.Stopped;
            control.StatusByName["audiocpp"] = AiServiceStatus.Starting;
            control.StatusByName["llama"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:8004"), control)
                .BuildPlanAsync(AiTaskKind.AudioGeneration, CancellationToken.None);

            Assert.Equal(["audiocpp"], plan.ToStart.Select(i => i.Service.Name));
            Assert.Equal(["llama"], plan.Conflicts.Select(s => s.Name));
            Assert.DoesNotContain(plan.Conflicts, s => s.Name == "audiocpp");
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
            // llama required and already Ready, but audiocpp (GPU) is still up holding VRAM.
            // The rival must be swept even though nothing needs starting.
            var control = ControlFor("llama");
            control.StatusByName["llama"] = AiServiceStatus.Ready;
            control.StatusByName["audiocpp"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:8080"), control)
                .BuildPlanAsync(AiTaskKind.CharacterAttribution, CancellationToken.None);

            Assert.False(plan.NothingToDo);
            Assert.Empty(plan.ToStart);
            Assert.Equal(["audiocpp"], plan.Conflicts.Select(s => s.Name));
        }

        [Fact]
        public async Task BuildPlan_VoiceDesignAudio_TtsReadyButLlamaUp_StopsLlama()
        {
            // Repro of the batch "generate audio for all characters" bug: audiocpp already answers
            // /health (Ready) while a leftover llama holds the GPU. Pre-flight must still stop llama.
            var control = ControlFor("audiocpp");
            control.StatusByName["audiocpp"] = AiServiceStatus.Ready;
            control.StatusByName["llama"] = AiServiceStatus.Ready;

            var plan = await Create(new StubResolver("http://localhost:8004"), control)
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

        [Fact]
        public async Task BuildPlan_OnlyUnmanagedEndpoints_NothingToDo_AndProbesNothing()
        {
            // An endpoint no managed container answers for (a hosted API, say) is never ours to start
            // or to sweep around, so the task runs straight away without a single status probe.
            var control = new FakeAiServiceControl { ResolveResult = null };

            var plan = await Create(new StubResolver("https://api.example.com"), control)
                .BuildPlanAsync(AiTaskKind.CharacterAttribution, CancellationToken.None);

            Assert.True(plan.NothingToDo);
            Assert.Equal(0, control.StatusCalls);
        }

        [Fact]
        public async Task BuildPlan_NoRequiredUrls_NothingToDo()
        {
            var control = new FakeAiServiceControl();

            var plan = await Create(new StubResolver(), control)
                .BuildPlanAsync(AiTaskKind.Transcription, CancellationToken.None);

            Assert.True(plan.NothingToDo);
            Assert.Empty(plan.ToStart);
            Assert.Empty(plan.Conflicts);
        }
    }
}
