using Read2Me.App.Services.Preflight;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Preflight
{
    public class AiPreflightProgressTests
    {
        private static DockerAiService Svc(string name, bool gpu = true) =>
            new(name, $"read2me-{name}", $"http://localhost:1{name.Length}00", "/docs", UsesGpu: gpu);

        private static AiPreflightPlan Plan(
            IEnumerable<DockerAiService>? toStart = null, IEnumerable<DockerAiService>? conflicts = null) =>
            new(
                (toStart ?? []).Select(s => new AiPreflightItem(s, AiServiceStatus.Stopped)).ToList(),
                (conflicts ?? []).ToList());

        [Fact]
        public void Load_OrdersConflictsBeforeServicesToStart()
        {
            var progress = new AiPreflightProgress(new FakeAiServiceControl());

            progress.Load(Plan(toStart: [Svc("whisper")], conflicts: [Svc("llama")]));

            Assert.Equal(["llama", "whisper"], progress.Rows.Select(r => r.Service.Name));
            Assert.True(progress.Rows[0].IsConflict);
            Assert.False(progress.Rows[1].IsConflict);
            Assert.All(progress.Rows, r => Assert.Equal(AiPreflightProgress.ServiceStage.Pending, r.Stage));
        }

        [Fact]
        public async Task RunAsync_HappyPath_StopsConflictsBeforeStarting_ReturnsTrue()
        {
            var control = new FakeAiServiceControl();
            var progress = new AiPreflightProgress(control);
            progress.Load(Plan(toStart: [Svc("chatterbox"), Svc("whisper")], conflicts: [Svc("llama")]));

            var ok = await progress.RunAsync(CancellationToken.None);

            Assert.True(ok);
            Assert.False(progress.HasFailed);
            Assert.Equal(["shutdown:llama", "start:chatterbox", "start:whisper"], control.OpLog);
            Assert.Equal(AiPreflightProgress.ServiceStage.Stopped, progress.Rows[0].Stage);
            Assert.Equal(AiPreflightProgress.ServiceStage.Ready, progress.Rows[1].Stage);
            Assert.Equal(AiPreflightProgress.ServiceStage.Ready, progress.Rows[2].Stage);
            Assert.False(progress.IsWorking);
        }

        [Fact]
        public async Task RunAsync_StartFailure_MarksRowFailed_SkipsRemaining_ReturnsFalse()
        {
            var control = new FakeAiServiceControl();
            control.StartResultByName["chatterbox"] = new AiServiceOpResult(false, AiServiceStatus.Down, "boom");
            var progress = new AiPreflightProgress(control);
            progress.Load(Plan(toStart: [Svc("chatterbox"), Svc("whisper")]));

            var ok = await progress.RunAsync(CancellationToken.None);

            Assert.False(ok);
            Assert.True(progress.HasFailed);
            Assert.Contains("chatterbox", progress.FailureMessage);
            Assert.Contains("boom", progress.FailureMessage);
            Assert.Equal(AiPreflightProgress.ServiceStage.Failed, progress.Rows[0].Stage);
            Assert.Equal("boom", progress.Rows[0].Error);
            Assert.Equal(AiPreflightProgress.ServiceStage.Pending, progress.Rows[1].Stage);
            Assert.DoesNotContain("start:whisper", control.OpLog);
        }

        [Fact]
        public async Task RunAsync_ConflictShutdownFailure_AbortsWithoutStartingAnything()
        {
            var control = new FakeAiServiceControl();
            control.ShutdownResultByName["llama"] = new AiServiceOpResult(false, AiServiceStatus.Unknown, "stuck");
            var progress = new AiPreflightProgress(control);
            progress.Load(Plan(toStart: [Svc("chatterbox")], conflicts: [Svc("llama")]));

            var ok = await progress.RunAsync(CancellationToken.None);

            Assert.False(ok);
            Assert.True(progress.HasFailed);
            Assert.Equal(AiPreflightProgress.ServiceStage.Failed, progress.Rows[0].Stage);
            Assert.Equal(AiPreflightProgress.ServiceStage.Pending, progress.Rows[1].Stage);
            Assert.DoesNotContain(control.OpLog, o => o.StartsWith("start:"));
        }

        [Fact]
        public async Task RunAsync_RaisesChangedOnEveryTransition()
        {
            var control = new FakeAiServiceControl();
            var progress = new AiPreflightProgress(control);
            progress.Load(Plan(toStart: [Svc("whisper")]));

            var changes = 0;
            progress.Changed += () => changes++;

            await progress.RunAsync(CancellationToken.None);

            // Working-start, Starting, Ready, working-end.
            Assert.True(changes >= 4);
        }

        [Fact]
        public async Task RunAsync_WhileOpInFlight_IsWorkingTrue()
        {
            var control = new FakeAiServiceControl { Gate = new TaskCompletionSource() };
            var progress = new AiPreflightProgress(control);
            progress.Load(Plan(toStart: [Svc("whisper")]));

            var run = progress.RunAsync(CancellationToken.None);

            Assert.True(progress.IsWorking);
            Assert.Equal(AiPreflightProgress.ServiceStage.Starting, progress.Rows[0].Stage);

            control.Gate.SetResult();
            Assert.True(await run);
            Assert.False(progress.IsWorking);
        }
    }
}
