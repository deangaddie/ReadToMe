using Read2Me.Services.Health;

namespace Read2Me.App.Services.Preflight
{
    /// <summary>A required service that is not Ready, with the status it was in when checked.</summary>
    public sealed record AiPreflightItem(DockerAiService Service, AiServiceStatus Status);

    /// <summary>
    /// What pre-flight found for a task: the required managed services that need starting, and the
    /// GPU services currently running that the task does not need (stopped first to free VRAM).
    /// Unmanaged endpoints and already-Ready services never appear here.
    /// </summary>
    public sealed record AiPreflightPlan(
        IReadOnlyList<AiPreflightItem> ToStart,
        IReadOnlyList<DockerAiService> Conflicts)
    {
        public bool NothingToDo => ToStart.Count == 0 && Conflicts.Count == 0;
    }
}
