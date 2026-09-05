using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.State;
using Read2Me.App.State.Projection;
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
/// putting one back — a mutation implementation nobody registered, a Book View adapter that writes
/// behind its own projection, or a persisted-state reconciliation event reappearing beside the
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
    /// The Book View's MudBlazor adapter renders snapshots, submits intents and mutations, and maps
    /// typed outcomes. Handing it <see cref="BookMutations"/> would let it commit behind its own
    /// projection — which is exactly the second consistency model this architecture removed.
    /// </summary>
    [Fact]
    public void TheBookViewAdapter_DoesNotTakeBookMutations()
    {
        Assert.DoesNotContain(
            typeof(BookHierarchyPresenter).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(BookMutations));
    }

    /// <summary>
    /// Nor may a rendered component reach the write side directly, for the same reason: a gesture
    /// that skips the projection publishes no snapshot, so the page it came from would be the one
    /// place in the app still patching itself.
    /// </summary>
    [Fact]
    public void NoRenderedComponent_TakesBookMutations()
    {
        var components = typeof(BookHierarchyPresenter).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && typeof(IComponent).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(components);

        foreach (var component in components)
        {
            Assert.DoesNotContain(
                component.GetConstructors().SelectMany(c => c.GetParameters()),
                p => p.ParameterType == typeof(BookMutations));

            Assert.DoesNotContain(
                component.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                p => p.PropertyType == typeof(BookMutations) &&
                     p.GetCustomAttribute<InjectAttribute>() is not null);
        }
    }

    /// <summary>
    /// The one channel persisted Book state reconciles through: <see cref="BookMutations"/>
    /// publishes a <see cref="BookMutationReceipt"/>, and <see cref="BookViewProjection"/> is the
    /// only thing that listens. A second reconciliation subscriber — however it is named — would be
    /// a second answer to "what does the Book look like now", which is the model this architecture
    /// removed.
    /// <para>
    /// Queue status, the Audio Gen Stream and attribution progress are untouched by this rule: they
    /// describe live work rather than reconciling persisted Book state, and nothing here constrains
    /// how many things listen to them.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheProjection_ConsumesBookMutationReceipts()
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

        // BookMutations is the publisher; BookViewProjection is the subscriber. Nothing else.
        Assert.Equal([nameof(BookMutations), nameof(BookViewProjection)], consumers);
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

    /// <summary>
    /// The other half of ADR 0007's rule for the adapter: it "owns no refresh, patch, reseed, or
    /// selection rule". Those rules are not a shape reflection can see, but the seams they would
    /// have to be written against are — the loader that rebuilds a Book View, the tree state it
    /// rebuilds into, and the coordinator that decides what a selection may still contain. The
    /// adapter reaches none of them; the projection owns all three.
    /// </summary>
    [Theory]
    [InlineData("IBookProjectLoader")]
    [InlineData("BookTreeState")]
    [InlineData("BookSelectionCoordinator")]
    [InlineData("ISelectionCoordinator")]
    public void TheBookViewAdapter_DoesNotTakeAReconciliationSeam(string seamName)
    {
        Assert.DoesNotContain(
            typeof(BookHierarchyPresenter).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType.Name == seamName);
    }

    private static Assembly[] ProductionAssemblies =>
    [
        typeof(BookHierarchyPresenter).Assembly,
        typeof(BookMutations).Assembly,
        typeof(ProjectDbContext).Assembly,
    ];
}
