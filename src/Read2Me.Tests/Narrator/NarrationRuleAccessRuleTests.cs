using System.Text.RegularExpressions;
using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// ADR-0006 access rule: "is this narration?" is asked in one place, <c>NarrationRule</c>.
    /// Production code may <em>stamp</em> the narrator sentinel freely — that is just a speaker —
    /// but comparing an item's speaker against it inline is how the rule drifts apart across the
    /// readers, resolvers, handlers and views that all have to agree on it.
    /// <para>
    /// Two projections are allowlisted below: EF cannot compose an <c>Expression</c> variable
    /// inside a nested collection projection, so those spell the rule out and say so. Anything
    /// else naming the comparison is a consumer that forgot the seam.
    /// </para>
    /// </summary>
    public class NarrationRuleAccessRuleTests
    {
        /// <summary>
        /// The comparison the seam owns — either direction, any spacing: an <em>item's</em> speaker
        /// tested against the sentinel. A request's own CharacterId is excluded — <c>c</c> on a
        /// command, <c>mutation</c> on a Book mutation: the code that compares those is asking a
        /// different question, may this request touch the seed narrator row at all, which is
        /// ADR-0004's business rather than this rule's.
        /// </summary>
        private static readonly Regex SpeakerComparison =
            new(@"(?<!\bc)(?<!\bmutation)\.CharacterId\s*[!=]=\s*ProjectDbContext\.NarratorId", RegexOptions.Compiled);

        /// <summary>
        /// Source-root-relative paths allowed to spell the comparison out, and why. Paths, not bare
        /// file names. A later slice that earns an exemption adds its own entry here, under review.
        /// </summary>
        private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"Read2Me.Data\NarrationRule.cs"] = "the seam — the one place the rule lives",
            [@"Read2Me.Services\ProjectReader.Book.cs"] =
                "two nested collection projections EF cannot compose an Expression into",
        };

        [Fact]
        public void TheNarrationComparison_IsSpelledOutOnlyBySanctionedFiles()
        {
            var offenders = ProductionSourceFiles()
                .Where(f => SpeakerComparison.IsMatch(File.ReadAllText(f)))
                .Select(f => Path.GetRelativePath(SourceRoot(), f))
                .Where(rel => !Allowed.ContainsKey(rel))
                .Order()
                .ToList();

            Assert.True(offenders.Count == 0,
                "\"Is this narration?\" must be asked through NarrationRule (ADR-0006). " +
                "Unsanctioned files comparing a speaker to the sentinel inline: " + string.Join(", ", offenders));
        }

        [Fact]
        public void EveryAllowlistEntryStillSpellsItOut()
        {
            // A stale entry is a hole nobody notices.
            var stale = Allowed.Keys
                .Where(rel =>
                {
                    var path = Path.Combine(SourceRoot(), rel);
                    return !File.Exists(path) || !SpeakerComparison.IsMatch(File.ReadAllText(path));
                })
                .ToList();

            Assert.True(stale.Count == 0,
                "Allowlisted files that no longer spell the comparison out: " + string.Join(", ", stale));
        }

        /// <summary>Production only — tests and fixtures arrange the sentinel as a value.</summary>
        private static IEnumerable<string> ProductionSourceFiles() =>
            Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Read2Me.Tests{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Read2Me.E2eTests{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Read2Me.TestUtils{Path.DirectorySeparatorChar}"));

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
