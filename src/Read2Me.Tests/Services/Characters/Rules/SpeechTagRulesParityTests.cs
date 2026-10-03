using System.Text.Json;
using Read2Me.Services;
using Read2Me.Services.Characters.Rules;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters.Rules
{
    /// <summary>
    /// The C# port against the spike's JavaScript, item for item, on the frozen in-sample and
    /// held-out chapters. Book text is never committed: the fixture is written outside the repo by
    /// the spike's <c>lab/dump-rules.mjs</c>, and this test is skipped unless
    /// <c>R2M_RULES_PARITY</c> names that file. Covers tagging (ticket 04) and name discovery (ticket 05).
    /// </summary>
    public class SpeechTagRulesParityTests
    {
        private const string FixtureVariable = "R2M_RULES_PARITY";

        private sealed record Fixture(IReadOnlyList<FixtureChapter> Chapters);

        private sealed record FixtureChapter(
            string Set, string Book, string Chapter, IReadOnlyList<FixtureParagraph> Paragraphs,
            IReadOnlyList<FixtureRoster> Roster, IReadOnlyDictionary<Guid, FixtureTag> Expected,
            IReadOnlyList<FixtureDiscover>? Discover);

        /// <summary>One discovery run: the roster it saw (a drop set's, grown chapter by chapter) and what the lab found.</summary>
        private sealed record FixtureDiscover(
            string Dropset, IReadOnlyList<FixtureRoster> Roster, IReadOnlyList<FixtureName> Expected);

        private sealed record FixtureName(string Name, int Count, string Example);

        private sealed record FixtureParagraph(Guid Id, IReadOnlyList<FixtureItem> Items);

        private sealed record FixtureItem(Guid Id, string Text, bool IsDialog);

        private sealed record FixtureRoster(string Name, IReadOnlyList<string>? Aliases);

        private sealed record FixtureTag(string Speaker, string Rule);

        [Fact]
        public void Tags_every_chapter_exactly_as_the_lab_does()
        {
            var fixture = Load();

            var mismatches = new List<string>();
            var total = 0;
            foreach (var chapter in fixture.Chapters)
            {
                var actual = SpeechTagRules.TagChapter(Paragraphs(chapter), Roster(chapter.Roster));

                total += chapter.Expected.Count;
                var where = $"{chapter.Set}/{chapter.Book}/{chapter.Chapter}";
                foreach (var (id, tag) in chapter.Expected)
                {
                    if (!actual.TryGetValue(id, out var got))
                        mismatches.Add($"{where} {id}: missing {tag.Speaker}/{tag.Rule}");
                    else if (got != new RuleTag(tag.Speaker, tag.Rule))
                        mismatches.Add($"{where} {id}: {got.Speaker}/{got.Rule}, lab {tag.Speaker}/{tag.Rule}");
                }
                foreach (var (id, got) in actual.Where(a => !chapter.Expected.ContainsKey(a.Key)))
                    mismatches.Add($"{where} {id}: extra {got.Speaker}/{got.Rule}");
            }

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{fixture.Chapters.Count} chapters, {total} lab tags, {mismatches.Count} mismatches");
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        }

        [Fact]
        public void Discovers_the_same_names_as_the_lab_in_every_chapter_and_drop_set()
        {
            var fixture = Load();
            Assert.Contains(fixture.Chapters, c => c.Discover is { Count: > 0 });

            var mismatches = new List<string>();
            var found = new List<string>();
            foreach (var chapter in fixture.Chapters)
            {
                var paragraphs = Paragraphs(chapter);
                foreach (var run in chapter.Discover ?? [])
                {
                    var actual = SpeechTagRules.DiscoverNames(paragraphs, Roster(run.Roster))
                        .Select(d => new FixtureName(d.Name, d.Count, d.Example))
                        .ToList();
                    var where = $"{run.Dropset}/{chapter.Set}/{chapter.Book}/{chapter.Chapter}";
                    found.AddRange(actual.Select(d => $"{where}: {d.Name} ({d.Count}×)"));
                    if (!actual.SequenceEqual(run.Expected))
                        mismatches.Add(
                            $"{where}: [{string.Join(", ", actual)}], lab [{string.Join(", ", run.Expected)}]");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{found.Count} discovered, {mismatches.Count} mismatches\n{string.Join("\n", found)}");
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        }

        private static Fixture Load()
        {
            var path = Environment.GetEnvironmentVariable(FixtureVariable);
            if (string.IsNullOrEmpty(path))
                Assert.Skip($"{FixtureVariable} is not set (fixture from the spike's lab/dump-rules.mjs)");

            var fixture = JsonSerializer.Deserialize<Fixture>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.NotEmpty(fixture.Chapters);
            return fixture;
        }

        private static List<ChapterParagraph> Paragraphs(FixtureChapter chapter) =>
            [.. chapter.Paragraphs.Select(p => new ChapterParagraph(p.Id, [.. p.Items.Select(ToContextItem)]))];

        private static List<RosterEntry> Roster(IReadOnlyList<FixtureRoster> roster) =>
            [.. roster.Select(r => new RosterEntry(r.Name, r.Aliases ?? []))];

        private static ContextItem ToContextItem(FixtureItem item) =>
            item.IsDialog
                ? new ContextItem(item.Id, item.Text, AttributionWire.Dialog, AttributionWire.Unknown)
                : new ContextItem(item.Id, item.Text, AttributionWire.Narration, AttributionWire.Narrator);
    }
}
