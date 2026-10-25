using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class DeterministicTransformerTests
    {
        [Theory]
        [InlineData("Chapter I. Intro", @"^Chapter [IVXLC]+\.\s*", "", "Intro")]
        [InlineData("Hello world", "world", "there", "Hello there")]
        [InlineData("abc123def", @"(\d+)", "[$1]", "abc[123]def")]
        [InlineData("no match", "xyz", "q", "no match")]
        public void RegexReplace_AppliesPattern(string old, string pattern, string replacement, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.RegexReplace(old, pattern, replacement));
        }

        [Fact]
        public void RegexReplace_NullReplacement_RemovesMatch()
        {
            Assert.Equal("Intro", DeterministicTransformer.RegexReplace("1. Intro", @"^\d+\.\s*", null));
        }

        [Theory]
        [InlineData("Part one", null, CaseMode.Upper, "PART ONE")]
        [InlineData("PART One", null, CaseMode.Lower, "part one")]
        [InlineData("Sherlock Holmes said", "Holmes", CaseMode.Upper, "Sherlock HOLMES said")]
        [InlineData("THE KING and THE QUEEN", @"\bTHE\b", CaseMode.Lower, "the KING and the QUEEN")]
        [InlineData("ALREADY UPPER", null, CaseMode.Upper, "ALREADY UPPER")]
        [InlineData("no match here", "xyz", CaseMode.Upper, "no match here")]
        [InlineData("élan vital", null, CaseMode.Upper, "ÉLAN VITAL")]
        public void ChangeCase_UpperLower_ReCasesWholeValueOrMatchedSpans(
            string value, string? pattern, CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, pattern, mode, ProtectionSet.Empty));
        }

        [Theory]
        // Sentence starts: value start, after leading whitespace / opening quotes, after . ! ? : (+ closing quotes)
        [InlineData("THE FIVE ORANGE PIPS", "The five orange pips")]
        [InlineData("THE", "The")]
        [InlineData("  ‘HELLO THERE’", "  ‘Hello there’")]
        [InlineData("STOP. GO NOW", "Stop. Go now")]
        [InlineData("WAIT! WHO? ME: YES", "Wait! Who? Me: Yes")]
        [InlineData("“STOP.” GO NOW", "“Stop.” Go now")]
        [InlineData("HE SAID “GO HOME” AGAIN", "He said “Go home” again")]
        [InlineData("JONATHAN HARKER’S JOURNAL—CONTINUED", "Jonathan harker’s journal—continued")]
        [InlineData("“ORANGE MARMALADE”", "“Orange marmalade”")]
        // Word splitting: apostrophes, hyphens, attached punctuation
        [InlineData("BINGLEY'S LETTER", "Bingley's letter")]
        [InlineData("BINGLEY’S LETTER", "Bingley’s letter")]
        [InlineData("SELF-STYLED HERO", "Self-styled hero")]
        [InlineData("A SELF-STYLED HERO", "A self-styled hero")]
        [InlineData("JONATHAN HARKER’S JOURNAL—continued", "Jonathan harker’s journal—continued")]
        // Fixed rules: roman numerals and pronoun I, short isolated acronyms, dotted acronyms
        [InlineData("PART IV", "Part IV")]
        [InlineData("KING LOUIS XII", "King louis XII")]
        [InlineData("WHAT I SAW", "What I saw")]
        [InlineData("I’M HERE", "I’m here")]
        [InlineData("A MIX OF DIV", "A mix of div")]
        [InlineData("IIII VX", "Iiii vx")]
        [InlineData("where UV light is", "Where UV light is")]
        [InlineData("UV LIGHT", "Uv light")]
        [InlineData("SEE THE UV", "See the uv")]
        [InlineData("THE U.S.A. IS BIG", "The U.S.A. Is big")]
        [InlineData("IN 300 A.D. THE EMPIRE", "In 300 A.D. The empire")]
        [InlineData("PG1661 B47", "PG1661 B47")]
        public void ChangeCase_Sentence_WholeValue(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, null, CaseMode.Sentence, ProtectionSet.Empty));
        }

        [Theory]
        [InlineData("THE FIVE ORANGE PIPS", "The Five Orange Pips")]
        [InlineData("THE", "The")]
        [InlineData("“ORANGE MARMALADE”", "“Orange Marmalade”")]
        [InlineData("THE MAN WITH THE TWISTED LIP", "The Man With the Twisted Lip")]
        [InlineData("A TALE OF A AN THE AND BUT OR NOR FOR OF TO IN ON AT BY AS",
            "A Tale of a an the and but or nor for of to in on at by as")]
        [InlineData("GONE. THE END", "Gone. The End")]
        [InlineData("WHERE TO", "Where to")]
        [InlineData("SELF-STYLED HERO", "Self-Styled Hero")]
        [InlineData("MOTHER-IN-LAW", "Mother-in-Law")]
        [InlineData("BINGLEY'S LETTER", "Bingley's Letter")]
        [InlineData("PART IV", "Part IV")]
        [InlineData("LOUIS XII", "Louis XII")]
        [InlineData("where UV light is", "Where UV Light Is")]
        [InlineData("THE U.S.A. STORY", "The U.S.A. Story")]
        [InlineData("chapter one", "Chapter One")]
        public void ChangeCase_Title_WholeValue(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, null, CaseMode.Title, ProtectionSet.Empty));
        }

        private const string AllCapsPattern = EditProgramSchema.AllCapsPattern;

        [Theory]
        [InlineData("Louis XII divorced his wife, Jeanne, daughter of Louis XI", CaseMode.Sentence, "Louis XII divorced his wife, Jeanne, daughter of Louis XI")]
        [InlineData("married in 1499 Anne of Brittany, widow of Charles VIII, in order", CaseMode.Sentence, "married in 1499 Anne of Brittany, widow of Charles VIII, in order")]
        [InlineData("which has the main PG search facility", CaseMode.Sentence, "which has the main PG search facility")]
        [InlineData("daughter of Louis XI, and married", CaseMode.Title, "daughter of Louis XI, and married")]
        [InlineData("“ORANGE MARMALADE”", CaseMode.Sentence, "“Orange marmalade”")]
        [InlineData("“ORANGE MARMALADE”", CaseMode.Title, "“Orange Marmalade”")]
        [InlineData("THE FIVE ORANGE PIPS", CaseMode.Sentence, "The five orange pips")]
        [InlineData("THE FIVE ORANGE PIPS", CaseMode.Title, "The Five Orange Pips")]
        [InlineData("JONATHAN HARKER’S JOURNAL—continued", CaseMode.Sentence, "Jonathan harker’s journal—continued")]
        [InlineData("Then he SHOUTED LOUDLY at Holmes", CaseMode.Sentence, "Then he shouted loudly at Holmes")]
        [InlineData("Then he SHOUTED LOUDLY at Holmes", CaseMode.Title, "Then he Shouted Loudly at Holmes")]
        public void ChangeCase_SentenceAndTitle_WithPattern_ReCasesOnlyMatchedSpans(
            string value, CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, AllCapsPattern, mode, ProtectionSet.Empty));
        }

        [Fact]
        public void ChangeCase_SentenceWithPattern_ReadsSentenceStartFromWholeValue()
        {
            Assert.Equal("He left. Then CAME the",
                DeterministicTransformer.ChangeCase("He left. THEN CAME the", "THEN", CaseMode.Sentence, ProtectionSet.Empty));
        }

        [Theory]
        [InlineData("Chapter {n}: {old}", 3, "The Storm", "Chapter 3: The Storm")]
        [InlineData("{old}!", 1, "Hello", "Hello!")]
        [InlineData("Part {n}", 12, "ignored", "Part 12")]
        [InlineData("No tokens", 5, "x", "No tokens")]
        public void RenderTemplate_SubstitutesTokens(string template, int n, string old, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.RenderTemplate(template, n, old));
        }
    }
}
