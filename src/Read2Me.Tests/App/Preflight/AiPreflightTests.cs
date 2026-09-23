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
            new(new AiPreflightPlanner(resolver, control, Registry), dialogs ?? Substitute.For<IDialogService>());

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
    }
}
