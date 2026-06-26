using System;

namespace Read2Me.App.Characters;

public abstract record VoiceBatchEvent;
public sealed record BatchStarted(string Operation, int Total) : VoiceBatchEvent;
public sealed record VoiceUpdated(Guid CharacterId, Guid VoiceId, string? DesignPrompt, string? AudioFileName, string? Transcript) : VoiceBatchEvent;
public sealed record BatchProgress(int Processed, int Total, int Failed, string? CurrentVoiceName) : VoiceBatchEvent;
public sealed record BatchCompleted(int Processed, int Failed) : VoiceBatchEvent;
public sealed record BatchCancelled : VoiceBatchEvent;
