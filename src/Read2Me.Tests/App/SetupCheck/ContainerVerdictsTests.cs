using Read2Me.App.Services.SetupCheck;
using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The container status × tier table: each status either ends a Docker services row with a final
    /// verdict or lets the check go on to probe (Quick) or act (Full). No runner, database or fakes.
    /// </summary>
    public class ContainerVerdictsTests
    {
        private static readonly DockerAiService Llama = new DockerAiServiceRegistry().All.Single(s => s.Name == "llama");

        [Theory]
        [InlineData(AiServiceStatus.NotFound, SetupCheckTier.Quick, SetupCheckResult.Failed, "No read2me-llama container")]
        [InlineData(AiServiceStatus.Down, SetupCheckTier.Quick, SetupCheckResult.Failed, "Down: the watchdog gave up restarting it")]
        [InlineData(AiServiceStatus.Unknown, SetupCheckTier.Quick, SetupCheckResult.NotRun, "docker didn't answer")]
        [InlineData(AiServiceStatus.Stopped, SetupCheckTier.Quick, SetupCheckResult.NotConfirmed, "Stopped")]
        [InlineData(AiServiceStatus.Recovering, SetupCheckTier.Quick, SetupCheckResult.NotConfirmed, "The watchdog is restarting it")]
        [InlineData(AiServiceStatus.Starting, SetupCheckTier.Quick, null, null)]
        [InlineData(AiServiceStatus.Ready, SetupCheckTier.Quick, null, null)]
        [InlineData(AiServiceStatus.NotFound, SetupCheckTier.Full, SetupCheckResult.Failed, "No read2me-llama container")]
        [InlineData(AiServiceStatus.Down, SetupCheckTier.Full, SetupCheckResult.Failed, "Down: the watchdog gave up restarting it")]
        [InlineData(AiServiceStatus.Unknown, SetupCheckTier.Full, SetupCheckResult.Failed, "docker didn't answer")]
        [InlineData(AiServiceStatus.Recovering, SetupCheckTier.Full, SetupCheckResult.Failed, "The watchdog is restarting it")]
        [InlineData(AiServiceStatus.Stopped, SetupCheckTier.Full, null, null)]
        [InlineData(AiServiceStatus.Starting, SetupCheckTier.Full, null, null)]
        [InlineData(AiServiceStatus.Ready, SetupCheckTier.Full, null, null)]
        public void A_status_ends_the_row_or_lets_the_check_proceed(
            AiServiceStatus status, SetupCheckTier tier, SetupCheckResult? result, string? found)
        {
            var verdict = ContainerVerdicts.For(Llama, status, tier);

            Assert.Equal(result, verdict?.Result);
            Assert.Equal(found, verdict?.Found);
        }

        [Fact]
        public void Every_Full_verdict_is_Passed_or_Failed()
        {
            foreach (var status in Enum.GetValues<AiServiceStatus>())
            {
                var verdict = ContainerVerdicts.For(Llama, status, SetupCheckTier.Full);
                Assert.True(verdict is null or { Result: SetupCheckResult.Passed or SetupCheckResult.Failed },
                    $"{status}: {verdict?.Result}");
            }
        }

        [Theory]
        [InlineData(SetupCheckTier.Quick)]
        [InlineData(SetupCheckTier.Full)]
        public void A_missing_container_is_created_with_compose_from_Infra(SetupCheckTier tier)
        {
            var fix = ContainerVerdicts.For(Llama, AiServiceStatus.NotFound, tier)!.Fix;

            Assert.Equal("docker compose up -d llama", fix.Copy);
            Assert.Contains("Infra/", fix.Text);
        }

        [Theory]
        [InlineData(SetupCheckTier.Quick)]
        [InlineData(SetupCheckTier.Full)]
        public void A_down_container_s_logs_are_read_then_it_is_brought_up_with_compose(SetupCheckTier tier)
        {
            var fix = ContainerVerdicts.For(Llama, AiServiceStatus.Down, tier)!.Fix;

            Assert.Equal("docker logs -f read2me-llama", fix.Copy);
            Assert.Contains("docker compose up -d llama", fix.Text);
        }

        [Theory]
        [InlineData(SetupCheckTier.Quick, "Check that Docker Desktop is running, then run the Quick check again.")]
        [InlineData(SetupCheckTier.Full, "Check that Docker Desktop is running, then run the Full check again.")]
        public void When_docker_can_t_tell_the_fix_names_the_check_to_run_again(SetupCheckTier tier, string text) =>
            Assert.Equal(text, ContainerVerdicts.For(Llama, AiServiceStatus.Unknown, tier)!.Fix.Text);

        [Theory]
        [InlineData(SetupCheckTier.Quick, "Wait for the restart, then run the Quick check again.")]
        [InlineData(SetupCheckTier.Full, "Wait for the restart, then run the Full check again.")]
        public void While_the_watchdog_restarts_it_the_fix_is_to_wait_then_run_the_check_again(SetupCheckTier tier, string text) =>
            Assert.Equal(text, ContainerVerdicts.For(Llama, AiServiceStatus.Recovering, tier)!.Fix.Text);

        [Fact]
        public void A_stopped_container_s_Quick_fix_points_to_the_Full_check() =>
            Assert.Contains("run the Full check", ContainerVerdicts.For(Llama, AiServiceStatus.Stopped, SetupCheckTier.Quick)!.Fix.Text);
    }
}
