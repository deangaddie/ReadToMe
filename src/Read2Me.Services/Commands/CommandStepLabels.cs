using Read2Me.Core.Models;

namespace Read2Me.Services.Commands;

/// <summary>
/// The History step label a <see cref="BookCommand"/> request records under, in the words the web
/// app uses for the gesture ("Edit with AI", "Split chapter", "Assign speaker"). The step's "what
/// changed" adds the counts and the place, so a label names the action and nothing more.
/// <para>
/// Results-only commands are named too, though they never record a step: every command has a
/// label, so a new one has to choose its words.
/// </para>
/// </summary>
public static class CommandStepLabels
{
    public static IReadOnlyDictionary<Type, string> All { get; } = new Dictionary<Type, string>
    {
        // structure
        [typeof(InsertNodeCommand)] = "Insert",
        [typeof(InsertParagraphItemCommand)] = "Insert text",
        [typeof(InsertPauseParagraphCommand)] = "Insert pause",
        [typeof(SplitAtPartCommand)] = "Split volume",
        [typeof(SplitAtChapterCommand)] = "Split part",
        [typeof(SplitAtParagraphCommand)] = "Split chapter",
        [typeof(SplitAtItemCommand)] = "Split paragraph",
        [typeof(MergeVolumeCommand)] = "Merge volumes",
        [typeof(MergePartCommand)] = "Merge parts",
        [typeof(MergeChapterCommand)] = "Merge chapters",
        [typeof(MergeParagraphCommand)] = "Merge paragraphs",
        [typeof(MergeParagraphItemCommand)] = "Merge text",
        [typeof(DeleteVolumeCommand)] = "Delete volume",
        [typeof(DeletePartCommand)] = "Delete part",
        [typeof(DeleteChapterCommand)] = "Delete chapter",
        [typeof(DeleteParagraphCommand)] = "Delete paragraph",
        [typeof(DeleteParagraphItemCommand)] = "Delete text",
        [typeof(ClearBookContentCommand)] = "Clear book",
        [typeof(AddTitlesCommand)] = "Add titles",
        [typeof(AddBookTitleCommand)] = "Add titles",
        [typeof(AddVolumeTitlesCommand)] = "Add titles",
        [typeof(AddPartTitlesCommand)] = "Add titles",
        [typeof(AddChapterTitlesCommand)] = "Add titles",
        [typeof(AddPausesCommand)] = "Add pauses",

        // text and titles
        [typeof(UpdateParagraphItemTextCommand)] = "Edit text",
        [typeof(UpdateVolumeTitleCommand)] = "Edit title",
        [typeof(UpdatePartTitleCommand)] = "Edit title",
        [typeof(UpdateChapterTitleCommand)] = "Edit title",
        [typeof(ApplyBookEditsCommand)] = "Edit with AI",
        [typeof(SetItemDeliveryCueCommand)] = "Edit delivery cue",

        // speakers and the roster
        [typeof(SetItemCharacterCommand)] = "Assign speaker",
        [typeof(SetParagraphCharacterCommand)] = "Assign speaker",
        [typeof(SetParagraphsCharacterCommand)] = "Assign speaker",
        [typeof(AttributeItemsCommand)] = "Assign speakers",
        [typeof(ReleaseNarrationCommand)] = "Release narration",
        [typeof(CreateCharacterCommand)] = "New character",
        [typeof(CreateCharacterAndAssignCommand)] = "New character",
        [typeof(RenameCharacterCommand)] = "Rename character",
        [typeof(AddCharacterAliasCommand)] = "Add alias",
        [typeof(RemoveCharacterAliasCommand)] = "Remove alias",
        [typeof(MergeCharactersCommand)] = "Merge characters",
        [typeof(DeleteCharacterCommand)] = "Delete character",
        [typeof(SetNarratorCharacterCommand)] = "Choose narrator",

        // voices and voice rules
        [typeof(CreateVoiceCommand)] = "New voice",
        [typeof(UpdateVoiceCommand)] = "Edit voice",
        [typeof(DeleteVoiceCommand)] = "Delete voice",
        [typeof(SetVoiceDefaultCommand)] = "Set default voice",
        [typeof(SetVoiceDesignPromptCommand)] = "Edit design prompt",
        [typeof(SetVoiceSettingsOverrideCommand)] = "Change voice settings",
        [typeof(SetVoiceTtsSettingsOverrideCommand)] = "Change voice settings",
        [typeof(SetVoiceTranscriptCommand)] = "Edit transcript",
        [typeof(SetVoiceAudioCommand)] = "Set voice audio",
        [typeof(SetVoiceGeneratedCommand)] = "Generate audio",
        [typeof(SetVoiceSourceCommand)] = "Change voice source",
        [typeof(CreateVoiceRuleCommand)] = "Add voice rule",
        [typeof(DeleteVoiceRuleCommand)] = "Delete voice rule",
        [typeof(MoveVoiceRuleCommand)] = "Move voice rule",

        // results only: never a step
        [typeof(SetParagraphItemAudioCommand)] = "Item audio",
        [typeof(SetAudioReviewCommand)] = "Audio review",
        [typeof(DismissAudioReviewCommand)] = "Dismiss audio review",
    };

    /// <summary>The command's label. A command missing from <see cref="All"/> falls back to its type name.</summary>
    public static string Of(BookCommand command) =>
        All.TryGetValue(command.GetType(), out var label) ? label : command.GetType().Name;
}
