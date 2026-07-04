using System.Threading;
using System.Threading.Tasks;
using Read2Me.App.Shared;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;
using Op = Read2Me.App.Shared.DockerServiceControlsPresenter.Op;

namespace Read2Me.Tests.App
{
    public class DockerServiceControlsPresenterTests
    {
        private static readonly DockerAiService Llama =
            new("llama", "read2me-llama", "http://localhost:8080", "/health");

        private static (DockerServiceControlsPresenter Presenter, FakeAiServiceControl Control) Managed(
            AiServiceStatus status = AiServiceStatus.Stopped)
        {
            var control = new FakeAiServiceControl { ResolveResult = Llama, StatusResult = status };
            var presenter = new DockerServiceControlsPresenter(control);
            presenter.Resolve("http://localhost:8080");
            return (presenter, control);
        }

        // ---- Resolve ----

        [Fact]
        public void Resolve_RemoteMiss_IsNotManaged()
        {
            var control = new FakeAiServiceControl { ResolveResult = null };
            var presenter = new DockerServiceControlsPresenter(control);

            Assert.False(presenter.Resolve("http://remote.example"));
            Assert.False(presenter.IsManaged);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_BlankUrl_SkipsFacadeAndIsNotManaged(string? url)
        {
            var control = new FakeAiServiceControl { ResolveResult = Llama };
            var presenter = new DockerServiceControlsPresenter(control);

            Assert.False(presenter.Resolve(url));
            Assert.False(presenter.IsManaged);
            Assert.Empty(control.ResolvedUrls);
        }

        [Fact]
        public void Resolve_RegistryHit_IsManaged()
        {
            var (presenter, _) = Managed();
            Assert.True(presenter.IsManaged);
            Assert.Same(Llama, presenter.Service);
        }

        // ---- Status fetch ----

        [Fact]
        public async Task RefreshStatus_StoresStatus_AndClearsBusy()
        {
            var (presenter, control) = Managed(AiServiceStatus.Ready);

            await presenter.RefreshStatusAsync(CancellationToken.None);

            Assert.Equal(AiServiceStatus.Ready, presenter.Status);
            Assert.False(presenter.IsBusy);
            Assert.Equal(1, control.StatusCalls);
        }

        // ---- Button visibility ----

        [Theory]
        [InlineData(AiServiceStatus.Stopped)]
        [InlineData(AiServiceStatus.NotFound)]
        public async Task StoppedOrNotFound_ShowsStartOnly(AiServiceStatus status)
        {
            var (presenter, _) = Managed(status);
            await presenter.RefreshStatusAsync(CancellationToken.None);

            Assert.True(presenter.CanStart);
            Assert.False(presenter.CanRestart);
            Assert.False(presenter.CanShutdown);
        }

        [Theory]
        [InlineData(AiServiceStatus.Ready)]
        [InlineData(AiServiceStatus.Starting)]
        [InlineData(AiServiceStatus.Down)]
        public async Task LiveOrDown_ShowsRestartAndShutdown(AiServiceStatus status)
        {
            var (presenter, _) = Managed(status);
            await presenter.RefreshStatusAsync(CancellationToken.None);

            Assert.False(presenter.CanStart);
            Assert.True(presenter.CanRestart);
            Assert.True(presenter.CanShutdown);
        }

        [Theory]
        [InlineData(AiServiceStatus.Recovering)]
        [InlineData(AiServiceStatus.Unknown)]
        public async Task RecoveringOrUnknown_ShowsNoLifecycleButtons(AiServiceStatus status)
        {
            var (presenter, _) = Managed(status);
            await presenter.RefreshStatusAsync(CancellationToken.None);

            Assert.False(presenter.CanStart);
            Assert.False(presenter.CanRestart);
            Assert.False(presenter.CanShutdown);
        }

        // ---- Lifecycle ops ----

        [Fact]
        public async Task Start_Success_AdoptsReadyStatus()
        {
            var (presenter, control) = Managed(AiServiceStatus.Stopped);
            control.StartResult = new AiServiceOpResult(true, AiServiceStatus.Ready, null);

            var result = await presenter.ExecuteAsync(Op.Start, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(AiServiceStatus.Ready, presenter.Status);
            Assert.Equal(new[] { "start" }, control.Ops);
            Assert.False(presenter.IsBusy);
        }

        [Fact]
        public async Task Restart_Failure_AdoptsFacadeStatus_AndReturnsError()
        {
            var (presenter, control) = Managed(AiServiceStatus.Ready);
            control.RestartResult = new AiServiceOpResult(false, AiServiceStatus.Down, "warm-up failed");

            var result = await presenter.ExecuteAsync(Op.Restart, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("warm-up failed", result.Error);
            Assert.Equal(AiServiceStatus.Down, presenter.Status);
        }

        [Fact]
        public async Task Shutdown_Success_AdoptsStoppedStatus()
        {
            var (presenter, control) = Managed(AiServiceStatus.Ready);

            var result = await presenter.ExecuteAsync(Op.Shutdown, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(AiServiceStatus.Stopped, presenter.Status);
            Assert.Equal(new[] { "shutdown" }, control.Ops);
        }

        [Fact]
        public async Task DuringOp_IsBusyWithLabel_ButtonsHidden()
        {
            var (presenter, control) = Managed(AiServiceStatus.Stopped);
            control.Gate = new TaskCompletionSource();

            var opTask = presenter.ExecuteAsync(Op.Start, CancellationToken.None);

            // Op is parked on the gate: busy is set, label reflects the op, nothing is actionable.
            Assert.True(presenter.IsBusy);
            Assert.Equal("Starting…", presenter.BusyLabel);
            Assert.False(presenter.CanStart);
            Assert.False(presenter.CanRefresh);

            control.Gate.SetResult();
            await opTask;

            Assert.False(presenter.IsBusy);
            Assert.Null(presenter.BusyLabel);
        }
    }
}
