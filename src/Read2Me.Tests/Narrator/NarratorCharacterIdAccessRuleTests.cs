using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// ADR-0004 access rule: the narrator link resolves at read time, so
    /// <c>Project.NarratorCharacterId</c> must be read only through
    /// <see cref="Read2Me.Data.NarratorIdentity"/>. The failure mode this guards is a
    /// consumer that reads the raw column, forgets the seam, and silently shows "Narrator".
    /// Writers are the sanctioned command handlers below.
    /// </summary>
    public class NarratorCharacterIdAccessRuleTests
    {
        /// <summary>
        /// Source-root-relative paths allowed to name the column, and why. Paths, not bare
        /// file names — a future <c>Project.cs</c> elsewhere must not slip through. A later
        /// slice that sanctions a writer adds its own entry here, under review.
        /// </summary>
        private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"Read2Me.Data\Entities\Project.cs"] = "declares the column",
            [@"Read2Me.Data\NarratorIdentity.cs"] = "the seam — the only reader",
            [@"Read2Me.Services\Commands\Handlers\NarratorHandlers.cs"] = "the sanctioned writer",
            [@"Read2Me.Services\Commands\Handlers\CharacterHandlers.cs"] = "delete clears the link, merge repoints it",
            [@"Read2Me.Tests\Services\Characters\DeleteCharacterHandlerTests.cs"] = "asserts delete cleared the column, not the fallback",
            [@"Read2Me.TestUtils\BookHierarchyBuilder.cs"] = "test Object Mother arranges the linked case",
            [@"Read2Me.Tests\Narrator\NarratorIdentityTests.cs"] = "covers the seam and the migration",
            [@"Read2Me.Tests\Narrator\NarrationSpeakerBackfillTests.cs"] = "names the AddNarratorCharacterId migration id, not the column",
            [@"Read2Me.Tests\Narrator\NarratorCharacterIdAccessRuleTests.cs"] = "this test",
        };

        [Fact]
        public void NarratorCharacterId_IsNamedOnlyBySanctionedFiles()
        {
            var offenders = SourceFiles()
                .Where(f => File.ReadAllText(f).Contains("NarratorCharacterId", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(SourceRoot(), f))
                .Where(rel => !Allowed.ContainsKey(rel))
                .Order()
                .ToList();

            Assert.True(offenders.Count == 0,
                "Project.NarratorCharacterId must be read only via NarratorIdentity.LoadAsync " +
                "(ADR-0004). Unsanctioned files naming it: " + string.Join(", ", offenders));
        }

        [Fact]
        public void EveryAllowlistEntryStillExists()
        {
            // A stale entry is a hole nobody notices.
            var missing = Allowed.Keys
                .Where(rel => !File.Exists(Path.Combine(SourceRoot(), rel)))
                .ToList();

            Assert.True(missing.Count == 0, "Allowlisted files that no longer exist: " + string.Join(", ", missing));
        }

        [Fact]
        public void TheSeamItselfStillReadsTheColumn()
        {
            // Guards the guard: if NarratorIdentity stops naming the column the rule above
            // passes vacuously.
            var seam = Directory.EnumerateFiles(SourceRoot(), "NarratorIdentity.cs", SearchOption.AllDirectories)
                .Single();

            Assert.Contains("NarratorCharacterId", File.ReadAllText(seam), StringComparison.Ordinal);
        }

        private static IEnumerable<string> SourceFiles() =>
            Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                // Migrations and the model snapshot are generated schema, not consumers.
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Read2Me.slnx")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
