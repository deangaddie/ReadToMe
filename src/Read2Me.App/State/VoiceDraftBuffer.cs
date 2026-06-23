using System;
using System.Collections.Generic;

namespace Read2Me.App.State;

public enum VoiceDraftField { Prompt, Transcript, Override, TtsOverride }

public sealed class VoiceDraftBuffer
{
    private readonly Dictionary<(Guid, VoiceDraftField), string> _drafts = new();

    /// Current draft value if edited, else the supplied saved value.
    public string Current(Guid voiceId, VoiceDraftField field, string? savedValue) =>
        _drafts.TryGetValue((voiceId, field), out var draft) ? draft : (savedValue ?? "");

    /// True when an edited draft differs from the saved value.
    public bool IsDirty(Guid voiceId, VoiceDraftField field, string? savedValue) =>
        _drafts.TryGetValue((voiceId, field), out var draft) && draft != (savedValue ?? "");

    /// Record an in-progress edit.
    public void Set(Guid voiceId, VoiceDraftField field, string value) =>
        _drafts[(voiceId, field)] = value;

    /// Drop the draft (after a successful save or cancel).
    public void Clear(Guid voiceId, VoiceDraftField field) =>
        _drafts.Remove((voiceId, field));
}
