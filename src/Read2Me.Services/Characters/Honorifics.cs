using System.Collections.Frozen;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// The honorific words ("mr", "doctor", "inspector"), lowercase and without their full stop. Two
    /// readers set them aside from a name's other words: the speech-tag rules, matching mentions in
    /// text against the roster, and the series' Link suggestions, matching names across books.
    /// </summary>
    public static class Honorifics
    {
        public static readonly FrozenSet<string> Words = FrozenSet.ToFrozenSet(
        [
            "mr", "mrs", "ms", "miss", "sir", "lady", "lord", "master", "prince", "princess", "duke", "duchess",
            "king", "queen", "father", "mother", "brother", "sister", "dr", "doctor", "captain", "colonel", "uncle", "aunt", "madame",
            "madam", "monsieur", "count", "countess", "baron", "baroness", "earl", "squire", "professor", "mistress", "dame", "general",
            "lieutenant", "sergeant", "major", "inspector", "reverend", "commander", "admiral", "baronet", "emperor", "empress",
        ], StringComparer.Ordinal);

        /// <summary>Whether a lowercase, stripped word is an honorific.</summary>
        public static bool Contains(string word) => Words.Contains(word);
    }
}
