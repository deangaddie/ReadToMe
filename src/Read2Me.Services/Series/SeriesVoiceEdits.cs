using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Which Book commits are edits of a Voice that a series copies to its other linked books
    /// (ADR 0018, #213 "Shared Voices"): creating, editing, re-recording and deleting a Voice. A
    /// Character's default (<see cref="SetVoiceDefaultMutation"/>) and its Voice Rules stay the
    /// book's own; a Restore (<see cref="RestoreStepMutation"/>) stays local to its book; and the
    /// series' own writes (<see cref="GainSeriesVoicesMutation"/>, <see cref="CopySeriesVoiceMutation"/>)
    /// are the copies themselves.
    /// </summary>
    public static class SeriesVoiceEdits
    {
        public abstract record Edit;

        /// <summary>A Voice made for <see cref="CharacterId"/>; its Id is the receipt's created Id.</summary>
        public sealed record Created(Guid CharacterId) : Edit;

        /// <summary>A Voice whose state changed and is copied whole.</summary>
        public sealed record Changed(Guid VoiceId) : Edit;

        /// <summary>A Voice removed; each other book removes it too.</summary>
        public sealed record Deleted(Guid VoiceId) : Edit;

        /// <summary>The Voice edit <paramref name="mutation"/> makes, or null when it is not one a series copies.</summary>
        public static Edit? Of(BookMutation mutation) => mutation switch
        {
            CreateVoiceMutation m => new Created(m.CharacterId),
            UpdateVoiceMutation m => new Changed(m.VoiceId),
            SetVoiceDesignPromptMutation m => new Changed(m.VoiceId),
            SetVoiceDesignSettingsOverrideMutation m => new Changed(m.VoiceId),
            SetVoiceTtsSettingsOverrideMutation m => new Changed(m.VoiceId),
            SetVoiceTranscriptMutation m => new Changed(m.VoiceId),
            SetVoiceAudioMutation m => new Changed(m.VoiceId),
            SetVoiceGeneratedMutation m => new Changed(m.VoiceId),
            ApplyVoiceAudioEditMutation m => new Changed(m.VoiceId),
            RestoreVoiceAudioOriginalMutation m => new Changed(m.VoiceId),
            SetVoiceSourceMutation m => new Changed(m.VoiceId),
            DeleteVoiceMutation m => new Deleted(m.VoiceId),
            _ => null,
        };

        /// <summary>The series' own writes into a book: never copied on, and never a reason to signal again.</summary>
        public static bool IsSeriesCopy(BookMutation mutation) =>
            mutation is GainSeriesVoicesMutation or CopySeriesVoiceMutation;

        /// <summary>
        /// The facets of a member book's commit that the series cast board shows (#213, Hub): its
        /// Characters and their names and aliases, the narrator, Voices and Voice Rules.
        /// </summary>
        public const BookFacets SignalledFacets =
            BookFacets.Characters | BookFacets.Narrator | BookFacets.Voices | BookFacets.VoiceRules;
    }
}
