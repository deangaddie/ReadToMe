using Read2Me.Core.Models;
using Read2Me.Services.History;

namespace Read2Me.Services.Commands;

/// <summary>
/// The History step label a <see cref="BookCommand"/> request records under, in the words the web
/// app uses for the gesture ("Edit with AI", "Split chapter", "Assign speaker"), and the step's
/// action, which picks its icon. The step's "what changed" adds the counts and the place, so a
/// label names the action and nothing more.
/// <para>
/// Results-only commands are named too, though they never record a step: every command has a
/// label, so a new one has to choose its words.
/// </para>
/// </summary>
public static class CommandStepLabels
{
    /// <summary>A command's step: its label and its action.</summary>
    public sealed record CommandStep(string Label, StepAction Action);

    public static IReadOnlyDictionary<Type, CommandStep> All { get; } = new Dictionary<Type, CommandStep>
    {
        // structure
        [typeof(InsertNodeCommand)] = new("Insert", StepAction.Insert),
        [typeof(InsertParagraphItemCommand)] = new("Insert text", StepAction.Insert),
        [typeof(InsertPauseParagraphCommand)] = new("Insert pause", StepAction.Pause),
        [typeof(SplitAtPartCommand)] = new("Split volume", StepAction.Split),
        [typeof(SplitAtChapterCommand)] = new("Split part", StepAction.Split),
        [typeof(SplitAtParagraphCommand)] = new("Split chapter", StepAction.Split),
        [typeof(SplitAtItemCommand)] = new("Split paragraph", StepAction.Split),
        [typeof(MergeVolumeCommand)] = new("Merge volumes", StepAction.Merge),
        [typeof(MergePartCommand)] = new("Merge parts", StepAction.Merge),
        [typeof(MergeChapterCommand)] = new("Merge chapters", StepAction.Merge),
        [typeof(MergeParagraphCommand)] = new("Merge paragraphs", StepAction.Merge),
        [typeof(MergeParagraphItemCommand)] = new("Merge text", StepAction.Merge),
        [typeof(DeleteVolumeCommand)] = new("Delete volume", StepAction.Delete),
        [typeof(DeletePartCommand)] = new("Delete part", StepAction.Delete),
        [typeof(DeleteChapterCommand)] = new("Delete chapter", StepAction.Delete),
        [typeof(DeleteParagraphCommand)] = new("Delete paragraph", StepAction.Delete),
        [typeof(DeleteParagraphItemCommand)] = new("Delete text", StepAction.Delete),
        [typeof(ClearBookContentCommand)] = new("Clear book", StepAction.Delete),
        [typeof(AddTitlesCommand)] = new("Add titles", StepAction.Title),
        [typeof(AddBookTitleCommand)] = new("Add titles", StepAction.Title),
        [typeof(AddVolumeTitlesCommand)] = new("Add titles", StepAction.Title),
        [typeof(AddPartTitlesCommand)] = new("Add titles", StepAction.Title),
        [typeof(AddChapterTitlesCommand)] = new("Add titles", StepAction.Title),
        [typeof(AddPausesCommand)] = new("Add pauses", StepAction.Pause),

        // text and titles
        [typeof(UpdateParagraphItemTextCommand)] = new("Edit text", StepAction.Edit),
        [typeof(UpdateVolumeTitleCommand)] = new("Edit title", StepAction.Title),
        [typeof(UpdatePartTitleCommand)] = new("Edit title", StepAction.Title),
        [typeof(UpdateChapterTitleCommand)] = new("Edit title", StepAction.Title),
        [typeof(ApplyBookEditsCommand)] = new("Edit with AI", StepAction.EditWithAi),
        [typeof(SetItemDeliveryCueCommand)] = new("Edit delivery cue", StepAction.Edit),

        // speakers and the roster
        [typeof(SetItemCharacterCommand)] = new("Assign speaker", StepAction.Character),
        [typeof(SetParagraphCharacterCommand)] = new("Assign speaker", StepAction.Character),
        [typeof(SetParagraphsCharacterCommand)] = new("Assign speaker", StepAction.Character),
        [typeof(AttributeItemsCommand)] = new("Assign speakers", StepAction.Character),
        [typeof(ReleaseNarrationCommand)] = new("Release narration", StepAction.Character),
        [typeof(CreateCharacterCommand)] = new("New character", StepAction.Character),
        [typeof(CreateCharacterAndAssignCommand)] = new("New character", StepAction.Character),
        [typeof(RenameCharacterCommand)] = new("Rename character", StepAction.Character),
        [typeof(AddCharacterAliasCommand)] = new("Add alias", StepAction.Character),
        [typeof(RemoveCharacterAliasCommand)] = new("Remove alias", StepAction.Delete),
        [typeof(MergeCharactersCommand)] = new("Merge characters", StepAction.Merge),
        [typeof(DeleteCharacterCommand)] = new("Delete character", StepAction.Delete),
        [typeof(SetNarratorCharacterCommand)] = new("Choose narrator", StepAction.Character),

        // voices and voice rules
        [typeof(CreateVoiceCommand)] = new("New voice", StepAction.Voice),
        [typeof(UpdateVoiceCommand)] = new("Edit voice", StepAction.Voice),
        [typeof(DeleteVoiceCommand)] = new("Delete voice", StepAction.Delete),
        [typeof(SetVoiceDefaultCommand)] = new("Set default voice", StepAction.Voice),
        [typeof(SetVoiceDesignPromptCommand)] = new("Edit design prompt", StepAction.Voice),
        [typeof(SetVoiceSettingsOverrideCommand)] = new("Change voice settings", StepAction.Voice),
        [typeof(SetVoiceTtsSettingsOverrideCommand)] = new("Change voice settings", StepAction.Voice),
        [typeof(SetVoiceTranscriptCommand)] = new("Edit transcript", StepAction.Voice),
        [typeof(SetVoiceAudioCommand)] = new("Set voice audio", StepAction.Voice),
        [typeof(SetVoiceGeneratedCommand)] = new("Generate audio", StepAction.Voice),
        [typeof(SetVoiceSourceCommand)] = new("Change voice source", StepAction.Voice),
        [typeof(CreateVoiceRuleCommand)] = new("Add voice rule", StepAction.Voice),
        [typeof(DeleteVoiceRuleCommand)] = new("Delete voice rule", StepAction.Delete),
        [typeof(MoveVoiceRuleCommand)] = new("Move voice rule", StepAction.Voice),

        // results only: never a step
        [typeof(SetParagraphItemAudioCommand)] = new("Item audio", StepAction.Edit),
        [typeof(SetAudioReviewCommand)] = new("Audio review", StepAction.Edit),
        [typeof(DismissAudioReviewCommand)] = new("Dismiss audio review", StepAction.Edit),
    };

    /// <summary>The command's step. A command missing from <see cref="All"/> falls back to its type name, as an edit.</summary>
    public static CommandStep Of(BookCommand command) =>
        All.TryGetValue(command.GetType(), out var step) ? step : new CommandStep(command.GetType().Name, StepAction.Edit);
}
