using Read2Me.Core.Models;

namespace Read2Me.Services.Queueing;

/// <summary>The kinds of work that make a project busy, in the order a busy answer names them.</summary>
public enum BusySourceKind { CharacterQueue, AudioQueue, VoiceBatch, Assembly }

/// <summary>One source of work queued or running for a project, with its user-facing reason.</summary>
public sealed record BusySource(BusySourceKind Kind, string Reason)
{
    /// <summary>"1 paragraph", "128 paragraphs".</summary>
    public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>
/// A project that has work queued or running. Never empty: an idle project has no busy answer at
/// all. <see cref="Reason"/> names every source, so one line can say why the project is busy.
/// </summary>
public sealed record ProjectBusy(IReadOnlyList<BusySource> Sources)
{
    public string Reason => string.Join(" · ", Sources.Select(s => s.Reason));
}

/// <summary>"Is anything queued or running for this project, and what?" Null means the project is idle.</summary>
public interface IProjectBusyAnswer
{
    ProjectBusy? For(ProjectFolderId folder);
}
