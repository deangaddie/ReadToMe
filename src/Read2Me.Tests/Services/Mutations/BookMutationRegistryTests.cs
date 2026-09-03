using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Mutations;

/// <summary>
/// The registry rule for the write-side spine: a mutation nobody can apply is not a runtime
/// surprise waiting for one caller to find it. This grows with each migrated family, and is what
/// keeps the final contraction honest once the legacy façade is gone.
/// </summary>
public class BookMutationRegistryTests : ProjectDbTestBase
{
    [Fact]
    public void EveryBookMutation_HasARegisteredImplementation()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        using var sp = services.BuildServiceProvider();

        var mutationTypes = typeof(BookMutation).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(BookMutation)) && !t.IsAbstract);

        foreach (var type in mutationTypes)
        {
            var contract = typeof(IBookMutationImplementation<>).MakeGenericType(type);
            Assert.True(sp.GetService(contract) != null, $"Mutation {type.Name} has no registered implementation.");
        }
    }
}
