using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;
using Xunit;
using static Read2Me.Services.Mutations.BookMutationRefusalKind;

namespace Read2Me.Tests.Services.Commands;

/// <summary>
/// The command table's own facts: every Book command has a row, and which Refusal kinds each
/// one-mutation command answers as <c>200 { "newEntityId": null }</c> (ADR 0007 keeps those fixed).
/// </summary>
public class BookCommandTableTests
{
    private static IEnumerable<Type> Commands() =>
        typeof(BookCommand).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(BookCommand)) && !t.IsAbstract);

    private static IEnumerable<string> MutationRowsAnswering(params BookMutationRefusalKind[] kinds) =>
        BookCommandTable.Rows.OfType<BookCommandTable.IMutationRow>()
            .Where(r => r.AnswersNullFor.Order().SequenceEqual(kinds.Order()))
            .Select(r => ((BookCommandTable.Row)r).CommandType.Name)
            .Order();

    [Fact]
    public void EveryBookCommand_HasARow()
    {
        Assert.Empty(Commands().Where(t => !BookCommandTable.Rows.Any(r => r.CommandType == t)).Select(t => t.Name));
    }

    [Fact]
    public void EveryRow_HasALabel()
    {
        Assert.Empty(BookCommandTable.Rows.Where(r => string.IsNullOrWhiteSpace(r.Label)).Select(r => r.CommandType.Name));
    }

    [Fact]
    public void TheStrictCommands_AnswerNoRefusalAsNull()
    {
        Assert.Equal(
            ["InsertNodeCommand", "SetItemDeliveryCueCommand", "SetNarratorCharacterCommand"],
            MutationRowsAnswering());
    }

    [Fact]
    public void TheRosterAndVoiceRuleCommands_AnswerMissingAndInvalidAsNull()
    {
        Assert.Equal(
            [
                "CreateVoiceRuleCommand", "DeleteCharacterCommand", "DeleteVoiceRuleCommand",
                "MergeCharactersCommand", "MoveVoiceRuleCommand", "RenameCharacterCommand",
            ],
            MutationRowsAnswering(Missing, Invalid));
    }

    [Fact]
    public void EveryOtherMutationRow_AnswersOnlyMissingAsNull()
    {
        var rows = BookCommandTable.Rows.OfType<BookCommandTable.IMutationRow>().ToList();

        Assert.Equal(
            rows.Count - 3 - 6,
            MutationRowsAnswering(Missing).Count());
    }

    [Fact]
    public void ACommand_HasItsRowsStep()
    {
        var row = BookCommandTable.Of(new SplitAtParagraphCommand(new ProjectFolderId("book"), Guid.NewGuid(), null));

        Assert.Equal(("Split chapter", Read2Me.Services.History.StepAction.Split), (row.Label, row.Action));
    }
}
