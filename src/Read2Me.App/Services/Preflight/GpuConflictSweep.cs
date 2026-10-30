using Read2Me.Services.Health;

namespace Read2Me.App.Services.Preflight
{
    /// <summary>
    /// The Preflight conflict sweep's rule: the single 8 GB card fits ~one model, so whenever a
    /// required service uses the GPU, every other GPU service still up is a conflict to stop first.
    /// Pre-flight plans with it, and the Setup check's Full check sweeps with it before each GPU visit.
    /// </summary>
    public static class GpuConflictSweep
    {
        /// <summary>The running GPU services <paramref name="required"/> doesn't include; none when it uses no GPU.</summary>
        public static async Task<IReadOnlyList<DockerAiService>> FindAsync(
            IAiServiceControl control, DockerAiServiceRegistry registry,
            IReadOnlyCollection<DockerAiService> required, CancellationToken ct)
        {
            var conflicts = new List<DockerAiService>();
            if (!required.Any(s => s.UsesGpu))
                return conflicts;

            var requiredNames = required.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in registry.All.Where(s => s.UsesGpu && !requiredNames.Contains(s.Name)))
            {
                var status = await control.GetStatusAsync(candidate, ct);
                if (status is AiServiceStatus.Ready or AiServiceStatus.Starting)
                    conflicts.Add(candidate);
            }
            return conflicts;
        }
    }
}
