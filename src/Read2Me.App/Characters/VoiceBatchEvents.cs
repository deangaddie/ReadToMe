using System;

namespace Read2Me.App.Characters;

public abstract record VoiceBatchEvent;
public sealed record BatchStarted(string Operation, int Total) : VoiceBatchEvent;
/// <summary>
/// One voice changed by a batch step. With fresh audio, <paramref name="ReferenceSeconds"/> and
/// <paramref name="ReferenceWarning"/> are the take's, as the voice list would read them.
/// </summary>
public sealed record VoiceUpdated(
    Guid CharacterId, Guid VoiceId, string? DesignPrompt, string? AudioFileName, string? Transcript,
    double? ReferenceSeconds = null, string? ReferenceWarning = null) : VoiceBatchEvent;
public sealed record BatchProgress(int Processed, int Total, int Failed, string? CurrentVoiceName) : VoiceBatchEvent;
public sealed record BatchCompleted(int Processed, int Failed) : VoiceBatchEvent;
public sealed record BatchCancelled : VoiceBatchEvent;
