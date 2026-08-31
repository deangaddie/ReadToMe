namespace Read2Me.Data.Enums
{
    /// <summary>
    /// What a ParagraphItem is, which is now exactly one question: is it a pause, or is it spoken?
    /// Who speaks a <see cref="Speech"/> item is the speaker's job — <c>CharacterId</c> null means
    /// unattributed dialog, the narrator sentinel means narration, anything else is that
    /// character's line (ADR-0006, <c>NarrationRule</c>). The former Narration/Character split
    /// duplicated that fact and could disagree with it, so it collapsed into this one member.
    /// Stored as text, so the numbering carries no meaning.
    /// </summary>
    public enum ParagraphItemType
    {
        Speech,
        VolumePause,
        PartPause,
        ChapterPause,
        ParagraphPause,
        Pause
    }
}
