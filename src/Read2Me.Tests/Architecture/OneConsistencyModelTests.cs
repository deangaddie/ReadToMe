using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Live;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Architecture;

/// <summary>
/// The rules that keep ADR 0007's one consistency model from quietly growing a second one. Every
/// producer-family slice retired its own legacy path; this file is what stops the next change from
/// putting one back — a mutation implementation nobody registered, a second subscriber reconciling
/// Book state from receipts, or a persisted-state reconciliation event reappearing beside the
/// receipt that replaced it.
/// <para>
/// The matching forward rule — every <see cref="BookMutation"/> has an implementation — lives in
/// <see cref="Services.Mutations.BookMutationRegistryTests"/>.
/// </para>
/// </summary>
public class OneConsistencyModelTests : ProjectDbTestBase
{
    private ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Written from the implementations rather than from the mutations, so an implementation added
    /// without a registration is caught even when its mutation is served by a different one.
    /// </summary>
    [Fact]
    public void EveryMutationImplementation_IsRegisteredForItsMutation()
    {
        using var sp = BuildContainer();

        var implementations = typeof(BookMutations).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType &&
                            i.GetGenericTypeDefinition() == typeof(IBookMutationImplementation<>))
                .Select(i => (Implementation: t, Contract: i)))
            .ToList();

        Assert.NotEmpty(implementations);

        foreach (var (implementation, contract) in implementations)
            Assert.True(
                sp.GetServices(contract).Any(s => s?.GetType() == implementation),
                $"{implementation.Name} is not registered for {contract.GetGenericArguments()[0].Name}.");
    }

    /// <summary>
    /// The one channel persisted Book state reconciles through: <see cref="BookMutations"/>
    /// publishes a <see cref="BookMutationReceipt"/>, and <see cref="LiveRelay"/> forwards it
    /// verbatim to hub clients. A host-side subscriber that derived Book state from receipts —
    /// however it is named — would be a second answer to "what does the Book look like now", which
    /// is the model this architecture removed.
    /// <para>
    /// Queue status, the Audio Gen Stream and attribution progress are untouched by this rule: they
    /// describe live work rather than reconciling persisted Book state, and nothing here constrains
    /// how many things listen to them.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheRelay_ConsumesBookMutationReceipts()
    {
        var consumers = ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(EventBroadcaster<BookMutationReceipt>)))
            .Select(t => t.Name)
            .Order()
            .ToList();

        // BookMutations is the publisher. LiveRelay derives no Book state from a receipt: the
        // Angular client reloads what each receipt names (ADR 0007, spec D6).
        Assert.Equal([nameof(BookMutations), nameof(LiveRelay)], consumers);
    }

    /// <summary>
    /// The events a receipt replaced, and the façade a mutation outcome replaced. Naming them by
    /// string is deliberate: the point is that nothing may reintroduce a type by these names, and a
    /// deleted type cannot be referred to any other way.
    /// </summary>
    [Theory]
    [InlineData("ParagraphItemsChanged")]
    [InlineData("AudioFileAssigned")]
    [InlineData("IBookCommandHandler")]
    [InlineData("BookCommandHandler")]
    public void ARetiredReconciliationType_HasNotComeBack(string typeName)
    {
        foreach (var assembly in ProductionAssemblies)
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Name == typeName);
    }

    private static Assembly[] ProductionAssemblies =>
    [
        typeof(LiveRelay).Assembly,
        typeof(BookMutations).Assembly,
        typeof(ProjectDbContext).Assembly,
    ];
}
