using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.App.Services.Preflight;

namespace Read2Me.Tests.Fakes
{
    /// <summary>Scriptable <see cref="IAiPreflight"/> — records calls, returns a canned answer.</summary>
    public sealed class FakeAiPreflight : IAiPreflight
    {
        public bool Result { get; set; } = true;
        public List<AiTaskKind> Calls { get; } = new();

        public Task<bool> EnsureReadyAsync(AiTaskKind task, CancellationToken ct = default)
        {
            Calls.Add(task);
            return Task.FromResult(Result);
        }
    }
}
