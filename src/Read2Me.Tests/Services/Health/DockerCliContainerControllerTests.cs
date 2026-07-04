using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class DockerCliContainerControllerTests
{
    private sealed record RunCall(string FileName, string Arguments);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<string, string, (int, string)> _respond;

        public FakeProcessRunner(Func<string, string, (int, string)> respond) => _respond = respond;

        public List<RunCall> Calls { get; } = new();
        public bool ThrowOnRun { get; init; }
        public Exception? ToThrow { get; init; }
        public bool ObserveCancellation { get; init; }

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(new RunCall(fileName, arguments));

            if (ObserveCancellation)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (ThrowOnRun)
            {
                throw ToThrow ?? new InvalidOperationException("docker not found");
            }

            return Task.FromResult(_respond(fileName, arguments));
        }
    }

    private static DockerCliContainerController Controller(FakeProcessRunner runner) =>
        new(runner, NullLogger<DockerCliContainerController>.Instance);

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task Lifecycle_InvokesDockerWithVerbAndContainer(string verb)
    {
        var runner = new FakeProcessRunner((_, _) => (0, "ok"));
        var controller = Controller(runner);

        var result = verb switch
        {
            "start" => await controller.StartAsync("read2me-llama", default),
            "stop" => await controller.StopAsync("read2me-llama", default),
            _ => await controller.RestartAsync("read2me-llama", default),
        };

        Assert.True(result.Succeeded);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal($"{verb} read2me-llama", call.Arguments);
    }

    [Fact]
    public async Task Lifecycle_ExitZero_Succeeds()
    {
        var runner = new FakeProcessRunner((_, _) => (0, "read2me-llama"));
        var result = await Controller(runner).StartAsync("read2me-llama", default);

        Assert.True(result.Succeeded);
        Assert.Equal("read2me-llama", result.Output);
    }

    [Fact]
    public async Task Lifecycle_NonZeroExit_FailsAndPreservesOutput()
    {
        var runner = new FakeProcessRunner((_, _) => (1, "Error: No such container: read2me-llama"));
        var result = await Controller(runner).StopAsync("read2me-llama", default);

        Assert.False(result.Succeeded);
        Assert.Equal("Error: No such container: read2me-llama", result.Output);
    }

    [Fact]
    public async Task Lifecycle_RunnerThrows_FailsWithExceptionMessage_NoThrow()
    {
        var runner = new FakeProcessRunner((_, _) => (0, ""))
        {
            ThrowOnRun = true,
            ToThrow = new InvalidOperationException("docker: command not found"),
        };

        var result = await Controller(runner).RestartAsync("read2me-llama", default);

        Assert.False(result.Succeeded);
        Assert.Equal("docker: command not found", result.Output);
    }

    [Theory]
    [InlineData("running", ContainerRunState.Running)]
    [InlineData("exited", ContainerRunState.Stopped)]
    [InlineData("created", ContainerRunState.Stopped)]
    [InlineData("paused", ContainerRunState.Stopped)]
    public async Task GetState_MapsInspectStatus(string status, ContainerRunState expected)
    {
        var runner = new FakeProcessRunner((_, _) => (0, status));
        var state = await Controller(runner).GetStateAsync("read2me-llama", default);

        Assert.Equal(expected, state);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal("inspect -f \"{{.State.Status}}\" read2me-llama", call.Arguments);
    }

    [Fact]
    public async Task GetState_NoSuchObject_ReturnsNotFound()
    {
        var runner = new FakeProcessRunner((_, _) => (1, "Error: No such object: read2me-llama"));
        var state = await Controller(runner).GetStateAsync("read2me-llama", default);

        Assert.Equal(ContainerRunState.NotFound, state);
    }

    [Fact]
    public async Task GetState_RunnerThrows_ReturnsUnknown_NoThrow()
    {
        var runner = new FakeProcessRunner((_, _) => (0, "")) { ThrowOnRun = true };
        var state = await Controller(runner).GetStateAsync("read2me-llama", default);

        Assert.Equal(ContainerRunState.Unknown, state);
    }

    [Fact]
    public async Task GetState_NonZeroWithoutNoSuchObject_ReturnsUnknown()
    {
        var runner = new FakeProcessRunner((_, _) => (1, "some other docker error"));
        var state = await Controller(runner).GetStateAsync("read2me-llama", default);

        Assert.Equal(ContainerRunState.Unknown, state);
    }

    [Fact]
    public async Task Lifecycle_Cancellation_PropagatesOperationCanceled()
    {
        var runner = new FakeProcessRunner((_, _) => (0, "ok")) { ObserveCancellation = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Controller(runner).StartAsync("read2me-llama", cts.Token));
    }

    [Fact]
    public async Task GetState_Cancellation_PropagatesOperationCanceled()
    {
        var runner = new FakeProcessRunner((_, _) => (0, "running")) { ObserveCancellation = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Controller(runner).GetStateAsync("read2me-llama", cts.Token));
    }
}
