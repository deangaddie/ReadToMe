using Read2Me.App.Services.Preflight;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Preflight
{
    public class AiPreflightDialogPresenterTests
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
            var presenter = new AiPreflightDialogPresenter(new FakeAiServiceControl());

            presenter.Load(Plan(toStart: [Svc("whisper")], conflicts: [Svc("llama")]));

            Assert.Equal(["llama", "whisper"], presenter.Rows.Select(r => r.Service.Name));
            Assert.True(presenter.Rows[0].IsConflict);
            Assert.False(presenter.Rows[1].IsConflict);
            Assert.All(presenter.Rows, r => Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Pending, r.Stage));
        }

        [Fact]
        public async Task RunAsync_HappyPath_StopsConflictsBeforeStarting_ReturnsTrue()
        {
            var control = new FakeAiServiceControl();
            var presenter = new AiPreflightDialogPresenter(control);
            presenter.Load(Plan(toStart: [Svc("chatterbox"), Svc("whisper")], conflicts: [Svc("llama")]));

            var ok = await presenter.RunAsync(CancellationToken.None);

            Assert.True(ok);
            Assert.False(presenter.HasFailed);
            Assert.Equal(["shutdown:llama", "start:chatterbox", "start:whisper"], control.OpLog);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Stopped, presenter.Rows[0].Stage);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Ready, presenter.Rows[1].Stage);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Ready, presenter.Rows[2].Stage);
            Assert.False(presenter.IsWorking);
        }

        [Fact]
        public async Task RunAsync_StartFailure_MarksRowFailed_SkipsRemaining_ReturnsFalse()
        {
            var control = new FakeAiServiceControl();
            control.StartResultByName["chatterbox"] = new AiServiceOpResult(false, AiServiceStatus.Down, "boom");
            var presenter = new AiPreflightDialogPresenter(control);
            presenter.Load(Plan(toStart: [Svc("chatterbox"), Svc("whisper")]));

            var ok = await presenter.RunAsync(CancellationToken.None);

            Assert.False(ok);
            Assert.True(presenter.HasFailed);
            Assert.Contains("chatterbox", presenter.FailureMessage);
            Assert.Contains("boom", presenter.FailureMessage);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Failed, presenter.Rows[0].Stage);
            Assert.Equal("boom", presenter.Rows[0].Error);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Pending, presenter.Rows[1].Stage);
            Assert.DoesNotContain("start:whisper", control.OpLog);
        }

        [Fact]
        public async Task RunAsync_ConflictShutdownFailure_AbortsWithoutStartingAnything()
        {
            var control = new FakeAiServiceControl();
            control.ShutdownResultByName["llama"] = new AiServiceOpResult(false, AiServiceStatus.Unknown, "stuck");
            var presenter = new AiPreflightDialogPresenter(control);
            presenter.Load(Plan(toStart: [Svc("chatterbox")], conflicts: [Svc("llama")]));

            var ok = await presenter.RunAsync(CancellationToken.None);

            Assert.False(ok);
            Assert.True(presenter.HasFailed);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Failed, presenter.Rows[0].Stage);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Pending, presenter.Rows[1].Stage);
            Assert.DoesNotContain(control.OpLog, o => o.StartsWith("start:"));
        }

        [Fact]
        public async Task RunAsync_RaisesChangedOnEveryTransition()
        {
            var control = new FakeAiServiceControl();
            var presenter = new AiPreflightDialogPresenter(control);
            presenter.Load(Plan(toStart: [Svc("whisper")]));

            var changes = 0;
            presenter.Changed += () => changes++;

            await presenter.RunAsync(CancellationToken.None);

            // Working-start, Starting, Ready, working-end.
            Assert.True(changes >= 4);
        }

        [Fact]
        public async Task RunAsync_WhileOpInFlight_IsWorkingTrue()
        {
            var control = new FakeAiServiceControl { Gate = new TaskCompletionSource() };
            var presenter = new AiPreflightDialogPresenter(control);
            presenter.Load(Plan(toStart: [Svc("whisper")]));

            var run = presenter.RunAsync(CancellationToken.None);

            Assert.True(presenter.IsWorking);
            Assert.Equal(AiPreflightDialogPresenter.ServiceStage.Starting, presenter.Rows[0].Stage);

            control.Gate.SetResult();
            Assert.True(await run);
            Assert.False(presenter.IsWorking);
        }
    }
}
