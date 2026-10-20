using System.Reflection;
using System.Text.RegularExpressions;
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

        // BookMutations is the publisher. LiveRelay derives no Book state from a receipt: the web
        // client reloads what each receipt names (ADR 0007, spec D6).
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

    /// <summary>
    /// History captures Project state at <see cref="BookMutations.CommitAsync"/>, and only state:
    /// a TTS take, its review and a Pipeline checkpoint are results and progress, not choices
    /// (context/history.md). So every mutation says which it is, by the family it derives from — a
    /// new one that derives from <see cref="BookMutation"/> directly has made no choice.
    /// </summary>
    [Fact]
    public void EveryMutation_DeclaresProjectStateOrResultsOnly()
    {
        var undeclared = ConcreteMutations
            .Where(t => !typeof(ProjectStateMutation).IsAssignableFrom(t) &&
                        !typeof(ResultsOnlyMutation).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(undeclared);
    }

    /// <summary>
    /// Pinned by name, so moving one of these to the state family — or a state mutation to this
    /// one — is a decision someone made on purpose rather than a slip History would pay for.
    /// </summary>
    [Fact]
    public void TheResultsOnlyMutations_AreTakesReviewsAndCheckpoints()
    {
        var resultsOnly = ConcreteMutations
            .Where(t => typeof(ResultsOnlyMutation).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order()
            .ToList();

        Assert.Equal(
            [
                nameof(DismissAudioReviewMutation),
                nameof(RecordParagraphItemAudioMutation),
                nameof(SetAudioReviewMutation),
                nameof(SetCheckpointMutation),
                nameof(SetParagraphItemAudioMutation),
            ],
            resultsOnly);
    }

    /// <summary>
    /// Every Project-state write commits through <see cref="BookMutations"/>, so History has one
    /// place to capture it (ADR 0016). Read from the source because a write is a call, which
    /// reflection cannot see: a production file that reaches the project database and saves, or runs
    /// a bulk update or delete, is writing around the seam — unless it is the seam's own
    /// implementations, or a system write listed below with the number of writes it is allowed.
    /// </summary>
    [Fact]
    public void ProjectDatabaseWrites_HappenOnlyInsideBookMutations()
    {
        var offenders = ProductionSourceFiles()
            .Select(f => (Path: Path.GetRelativePath(SourceTree.Root, f), Text: File.ReadAllText(f)))
            .Where(f => ProjectDbAccess.IsMatch(f.Text))
            .Where(f => !f.Path.StartsWith(MutationsFolder, StringComparison.OrdinalIgnoreCase))
            .Select(f => (f.Path, Writes: DatabaseWrite.Count(f.Text)))
            .Where(f => f.Writes > SystemWrites.GetValueOrDefault(f.Path))
            .Select(f => $"{f.Path} ({f.Writes} writes)")
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0,
            "Project-state writes must commit through BookMutations.CommitAsync as a Book mutation. " +
            "Writing outside it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EverySystemWriteEntry_StillExists()
    {
        // A stale entry is a hole nobody notices.
        var missing = SystemWrites.Keys
            .Where(rel => !File.Exists(Path.Combine(SourceTree.Root, rel)))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// Writes that are not a user's change to a project and stay outside History. Migrations at
    /// open run through <c>Database.Migrate</c>, which the scan does not count.
    /// </summary>
    private static readonly Dictionary<string, int> SystemWrites = new(StringComparer.OrdinalIgnoreCase)
    {
        // Creating a project writes its first Project row, before there is a Book to mutate.
        [Path.Combine("Read2Me.Services", "ProjectService.cs")] = 1,
        // Starting History copies the project into its base snapshot with VACUUM INTO: it reads
        // project.db and writes only the copy.
        [Path.Combine("Read2Me.Services", "History", "ProjectHistory.cs")] = 1,
    };

    private static readonly string MutationsFolder =
        Path.Combine("Read2Me.Services", "Mutations") + Path.DirectorySeparatorChar;

    private static readonly Regex ProjectDbAccess =
        new(@"\b(ProjectDbSession|IProjectDbContextFactory|ProjectDbContext)\b", RegexOptions.Compiled);

    private static readonly Regex DatabaseWrite =
        new(@"\b(SaveChanges|ExecuteUpdate|ExecuteDelete|ExecuteSql\w*)(Async)?\s*\(", RegexOptions.Compiled);

    private static IEnumerable<string> ProductionSourceFiles() =>
        new[] { "Read2Me.App", "Read2Me.Services", "Read2Me.Data" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(SourceTree.Root, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static IEnumerable<Type> ConcreteMutations =>
        typeof(BookMutation).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(BookMutation).IsAssignableFrom(t));

    private static Assembly[] ProductionAssemblies =>
    [
        typeof(LiveRelay).Assembly,
        typeof(BookMutations).Assembly,
        typeof(ProjectDbContext).Assembly,
    ];
}
