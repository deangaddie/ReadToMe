using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Xunit;

namespace Read2Me.Tests.Services.Commands;

public class CommandStepLabelsTests
{
    [Fact]
    public void EveryBookCommand_HasAHistoryStepLabel()
    {
        var commands = typeof(BookCommand).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(BookCommand)) && !t.IsAbstract);

        Assert.Empty(commands.Where(t => !CommandStepLabels.All.ContainsKey(t)).Select(t => t.Name));
    }
}
