using System.Text.RegularExpressions;
using Read2Me.Services.BookEdits;

namespace Read2Me.Tests.Services.BookEdits
{
    /// <summary>The pure scoring rules <see cref="EditPlanLiveEvalTests"/> applies to a plan.</summary>
    public static class EditPlanEvalScoring
    {
        /// <summary>A template rendered against fixture source values (the template analogue of
        /// span-matching for patterns): the production renderer, so tokens behave as in a real run.</summary>
        public static string Render(
            string template, IReadOnlyDictionary<string, string> fixture, int n = 1, string old = "") =>
            DeterministicTransformer.RenderTemplate(template, n, old,
                fixture.Select(kv => new EditSourceValue(kv.Key, kv.Value, null)).ToList());

        /// <summary>Source names compared as a set.</summary>
        public static bool SameSources(IEnumerable<string> expected, IEnumerable<EditSource> actual) =>
            expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Select(s => s.Name));

        /// <summary>True when the instruction mentions every name as a whole word.</summary>
        public static bool MentionsEach(string? instruction, IEnumerable<string> names) =>
            names.All(name => instruction != null
                && Regex.IsMatch(instruction, $@"(?<!\w){Regex.Escape(name)}(?!\w)", RegexOptions.CultureInvariant));
    }
}
