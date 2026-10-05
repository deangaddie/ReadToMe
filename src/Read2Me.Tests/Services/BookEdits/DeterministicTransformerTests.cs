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
        [InlineData("Hari Seldon said", "Seldon", CaseMode.Upper, "Hari SELDON said")]
        [InlineData("THE MULE and THE GENERAL", @"\bTHE\b", CaseMode.Lower, "the MULE and the GENERAL")]
        [InlineData("ALREADY UPPER", null, CaseMode.Upper, "ALREADY UPPER")]
        [InlineData("no match here", "xyz", CaseMode.Upper, "no match here")]
        [InlineData("élan vital", null, CaseMode.Upper, "ÉLAN VITAL")]
        public void ChangeCase_UpperLower_ReCasesWholeValueOrMatchedSpans(
            string value, string? pattern, CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, pattern, mode));
        }

        [Theory]
        // Sentence starts: value start, after leading whitespace / opening quotes, after . ! ? : (+ closing quotes)
        [InlineData("THE MAYORS", "The mayors")]
        [InlineData("THE", "The")]
        [InlineData("  ‘HELLO THERE’", "  ‘Hello there’")]
        [InlineData("STOP. GO NOW", "Stop. Go now")]
        [InlineData("WAIT! WHO? ME: YES", "Wait! Who? Me: Yes")]
        [InlineData("“STOP.” GO NOW", "“Stop.” Go now")]
        [InlineData("HE SAID “GO HOME” AGAIN", "He said “Go home” again")]
        [InlineData("SELDON—THE MAN", "Seldon—the man")]
        [InlineData("“TAXIS TO ALL POINTS.”", "“Taxis to all points.”")]
        // Word splitting: apostrophes, hyphens, attached punctuation
        [InlineData("SELDON'S PLAN", "Seldon's plan")]
        [InlineData("SELDON’S PLAN", "Seldon’s plan")]
        [InlineData("SELF-STYLED HERO", "Self-styled hero")]
        [InlineData("A SELF-STYLED HERO", "A self-styled hero")]
        [InlineData("HARI SELDON—born", "Hari seldon—born")]
        // Fixed rules: roman numerals and pronoun I, short isolated acronyms, dotted acronyms
        [InlineData("PART IV", "Part IV")]
        [InlineData("THE EMPEROR CLEON II", "The emperor cleon II")]
        [InlineData("WHAT I SAW", "What I saw")]
        [InlineData("I’M HERE", "I’m here")]
        [InlineData("A MIX OF DIV", "A mix of div")]
        [InlineData("IIII VX", "Iiii vx")]
        [InlineData("where UV light is", "Where UV light is")]
        [InlineData("UV LIGHT", "Uv light")]
        [InlineData("SEE THE UV", "See the uv")]
        [InlineData("THE U.S.A. IS BIG", "The U.S.A. Is big")]
        [InlineData("IN 300 A.D. THE EMPIRE", "In 300 A.D. The empire")]
        [InlineData("PS3551 F59", "PS3551 F59")]
        public void ChangeCase_Sentence_WholeValue(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, null, CaseMode.Sentence));
        }

        [Theory]
        [InlineData("THE MAYORS", "The Mayors")]
        [InlineData("THE", "The")]
        [InlineData("“TAXIS TO ALL POINTS.”", "“Taxis to All Points.”")]
        [InlineData("THE LORD OF THE RINGS", "The Lord of the Rings")]
        [InlineData("A TALE OF A AN THE AND BUT OR NOR FOR OF TO IN ON AT BY AS",
            "A Tale of a an the and but or nor for of to in on at by as")]
        [InlineData("GONE. THE END", "Gone. The End")]
        [InlineData("WHERE TO", "Where to")]
        [InlineData("SELF-STYLED HERO", "Self-Styled Hero")]
        [InlineData("MOTHER-IN-LAW", "Mother-in-Law")]
        [InlineData("SELDON'S PLAN", "Seldon's Plan")]
        [InlineData("PART IV", "Part IV")]
        [InlineData("CLEON II", "Cleon II")]
        [InlineData("where UV light is", "Where UV Light Is")]
        [InlineData("THE U.S.A. STORY", "The U.S.A. Story")]
        [InlineData("chapter one", "Chapter One")]
        public void ChangeCase_Title_WholeValue(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, null, CaseMode.Title));
        }

        private const string AllCapsPattern = @"\b\p{Lu}{2,}(?:['’]\p{Lu}+)?(?:[ -]+\p{Lu}+(?:['’]\p{Lu}+)?)*\b";

        [Theory]
        [InlineData("…the last strong Emperor, Cleon II. The first", CaseMode.Sentence, "…the last strong Emperor, Cleon II. The first")]
        [InlineData("but for the reign of Stannell VI, and he died", CaseMode.Sentence, "but for the reign of Stannell VI, and he died")]
        [InlineData("it works on Korell, where UV light is not", CaseMode.Sentence, "it works on Korell, where UV light is not")]
        [InlineData("land on Glyptal IV the day after", CaseMode.Title, "land on Glyptal IV the day after")]
        [InlineData("“TAXIS TO ALL POINTS.”", CaseMode.Sentence, "“Taxis to all points.”")]
        [InlineData("“TAXIS TO ALL POINTS.”", CaseMode.Title, "“Taxis to All Points.”")]
        [InlineData("THE MAYORS", CaseMode.Sentence, "The mayors")]
        [InlineData("THE MAYORS", CaseMode.Title, "The Mayors")]
        [InlineData("TRANTOR— . . . At the beginning of", CaseMode.Sentence, "Trantor— . . . At the beginning of")]
        [InlineData("Then he SHOUTED LOUDLY at Hari", CaseMode.Sentence, "Then he shouted loudly at Hari")]
        [InlineData("Then he SHOUTED LOUDLY at Hari", CaseMode.Title, "Then he Shouted Loudly at Hari")]
        public void ChangeCase_SentenceAndTitle_WithPattern_ReCasesOnlyMatchedSpans(
            string value, CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, AllCapsPattern, mode));
        }

        [Fact]
        public void ChangeCase_SentenceWithPattern_ReadsSentenceStartFromWholeValue()
        {
            Assert.Equal("He left. Then CAME the",
                DeterministicTransformer.ChangeCase("He left. THEN CAME the", "THEN", CaseMode.Sentence));
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
