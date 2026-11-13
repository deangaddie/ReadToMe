using System.Diagnostics;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>Polls for what a fire-and-forget run leaves behind, failing the test after ten seconds.</summary>
internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string waitingFor)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"timed out waiting for {waitingFor}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
