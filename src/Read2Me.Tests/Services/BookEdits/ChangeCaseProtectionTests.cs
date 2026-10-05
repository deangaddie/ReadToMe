using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    /// <summary>
    /// Sentence and title case keep the book's names: the protection set is learned from book text,
    /// then passed to the pure re-caser. Assertions are on re-cased values only.
    /// </summary>
    public class ChangeCaseProtectionTests
    {
        private const string AllCapsPattern = @"\b\p{Lu}{2,}(?:['’]\p{Lu}+)?(?:[ -]+\p{Lu}+(?:['’]\p{Lu}+)?)*\b";

        /// <summary>A Foundation-like book: the survey's all-caps values (which carry no case
        /// evidence) plus prose that uses the names mid-sentence and the ordinary words in lowercase.
        /// "four" and "kingdoms" are below the word threshold on their own, so only the phrase rule
        /// keeps "Four Kingdoms".</summary>
        private static readonly string[] FoundationBook =
        [
            "PART IV",
            "HARI SELDON— . . . born in the 11,988th year of the Galactic Era;",
            "ENCYCLOPEDIA GALACTICA",
            "“TAXIS TO ALL POINTS.”",
            "TRANTOR— . . . At the beginning of the thirteenth millennium,",
            "PSYCHOHISTORY— . . . Gaal Dornick, using nonmathematical concepts,",
            "…the last strong Emperor, Cleon II. The first Chief Commissioner.",
            "THE MAYORS",
            "THE FOUR KINGDOMS— The name given to those portions of the Province of Anacreon",
            "THE TRADERS",
            "THE MERCHANT",
            "PRINCES",
            "I’m just a sucker who happened to land on Glyptal IV the day after the mail.",
            "“It’s on Orsha II. Twenty parsecs off.",
            "but for the reign of Stannell VI, and he died fifty years ago.",
            "it works on Korell, where UV light is not to be found on street corners.",
            "KORELL— . . . And so after three years of a war",
            "ISAAC ASIMOV began his Foundation Series at the age of twenty-one,",
            "THE FOUNDATION NOVELS",
            "THE ROBOT NOVELS",
            "Gaal Dornick had met Hari Seldon on Trantor before he left for Terminus.",
            "He said Hari Seldon was a great man, and that the Foundation would last.",
            "The ships of Korell came in the night; the Encyclopedia Galactica was finished.",
            "It rested on a scientific foundation, as the Foundation itself did.",
            "the Robot series and the Foundation series were written by Isaac Asimov",
            "Not even the Four Kingdoms could stand; the Four Kingdoms fell, and later the Four Kingdoms rose.",
            "there were four kingdoms once",
            .. Enumerable.Repeat("the four winds blew over the kingdoms", 8),
            "the traders came, the traders left, as traders do, and the Traders guild said nothing",
            "a merchant, a merchant's son and a merchant's wife met the Merchant at the merchant hall",
            "psychohistory is a science of mobs",
        ];

        private static readonly ProtectionSet Foundation = ProtectionSet.Build(FoundationBook);

        [Theory]
        // The survey's 19 sample rows ("Simulation", protected column)
        [InlineData("HARI SELDON— . . . born in the 11,988th year", "Hari Seldon— . . . born in the 11,988th year")]
        [InlineData("ENCYCLOPEDIA GALACTICA", "Encyclopedia Galactica")]
        [InlineData("“TAXIS TO ALL POINTS.”", "“Taxis to all points.”")]
        [InlineData("TRANTOR— . . . At the beginning", "Trantor— . . . At the beginning")]
        [InlineData("PSYCHOHISTORY— . . . Gaal Dornick", "Psychohistory— . . . Gaal Dornick")]
        [InlineData("…the last strong Emperor, Cleon II. The first", "…the last strong Emperor, Cleon II. The first")]
        [InlineData("THE MAYORS", "The mayors")]
        [InlineData("THE FOUR KINGDOMS— The name", "The Four Kingdoms— The name")]
        [InlineData("THE TRADERS", "The traders")]
        [InlineData("THE MERCHANT", "The merchant")]
        [InlineData("land on Glyptal IV the day", "land on Glyptal IV the day")]
        [InlineData("“It’s on Orsha II. Twenty", "“It’s on Orsha II. Twenty")]
        [InlineData("reign of Stannell VI, and", "reign of Stannell VI, and")]
        [InlineData("where UV light is not", "where UV light is not")]
        [InlineData("KORELL— . . .", "Korell— . . .")]
        [InlineData("ISAAC ASIMOV began", "Isaac Asimov began")]
        [InlineData("THE FOUNDATION NOVELS", "The Foundation novels")]
        [InlineData("THE ROBOT NOVELS", "The Robot novels")]
        [InlineData("PART IV", "Part IV")]
        public void Sentence_FoundationSurveyRows(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, AllCapsPattern, CaseMode.Sentence, Foundation));
        }

        [Theory]
        [InlineData("THE FOUR KINGDOMS", "The Four Kingdoms")]
        [InlineData("THE MAYORS OF TERMINUS", "The Mayors of Terminus")]
        [InlineData("HARI SELDON AND THE FOUNDATION", "Hari Seldon and the Foundation")]
        public void Title_KeepsNamesAndSmallWords(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, null, CaseMode.Title, Foundation));
        }

        [Theory]
        // Word rule edges: mid/(mid+lower) ≥ 0.3 is in, below is out, mid = 0 is out.
        [InlineData(3, 7, "We saw Vale")]
        [InlineData(2, 5, "We saw vale")]
        [InlineData(0, 0, "We saw vale")]
        public void Sentence_WordThresholdEdges(int mid, int lower, string expected)
        {
            var book = Enumerable.Repeat("they reached Vale at dusk", mid)
                .Concat(Enumerable.Repeat("they reached the vale at dusk", lower))
                .Append("Vale was quiet."); // sentence-initial: no evidence
            Assert.Equal(expected, ChangeCase("WE SAW VALE", CaseMode.Sentence, book));
        }

        [Theory]
        [InlineData(3, 7, "The Iron Gate")]
        [InlineData(2, 5, "The iron gate")]
        public void Sentence_PhraseThresholdEdges(int mid, int lower, string expected)
        {
            var book = Enumerable.Repeat("they passed the Iron Gate", mid)
                .Concat(Enumerable.Repeat("they passed the iron gate", lower));
            Assert.Equal(expected, ChangeCase("THE IRON GATE", CaseMode.Sentence, book));
        }

        [Fact]
        public void Sentence_PhraseNeverCapitalisedMidSentence_IsNotKept()
        {
            string[] book = ["Iron Gate stood.", "Iron Gate fell.", "they passed the iron gate", .. Enumerable.Repeat("a gate", 5)];
            Assert.Equal("The iron gate", ChangeCase("THE IRON GATE", CaseMode.Sentence, book));
        }

        [Fact]
        public void SentenceInitialRun_CountsTheWordsAfterTheFirst()
        {
            string[] book = ["The Iron Gate fell.", .. Enumerable.Repeat("iron bars and a gate", 5)];
            Assert.Equal("Past the Iron Gate", ChangeCase("PAST THE IRON GATE", CaseMode.Sentence, book));
        }

        [Fact]
        public void LongestPhrase_WinsOverAShorterOneAtTheSameStart()
        {
            string[] book =
            [
                "they met at the Hall of Records today",
                "they met at the Hall of Records Annex today",
                .. Enumerable.Repeat("the records annex", 5),
            ];
            Assert.Equal("The Hall of Records Annex", ChangeCase("THE HALL OF RECORDS ANNEX", CaseMode.Sentence, book));
            Assert.Equal("The Hall of Records and the annex",
                ChangeCase("THE HALL OF RECORDS AND THE ANNEX", CaseMode.Sentence, book));
        }

        [Fact]
        public void RestoreForm_TieDoesNotDependOnTheBooksOrder()
        {
            string[] book = ["they met MacArthur there", "they met Macarthur there"];
            Assert.Equal(
                ChangeCase("ASK MACARTHUR", CaseMode.Sentence, book),
                ChangeCase("ASK MACARTHUR", CaseMode.Sentence, book.Reverse()));
        }

        [Fact]
        public void AllCapsOccurrences_AreNoEvidence()
        {
            Assert.Equal("We saw vale", ChangeCase("WE SAW VALE", CaseMode.Sentence, ["VALE", "they reached VALE"]));
        }

        [Fact]
        public void PhraseWithConnector_KeepsWordsBelowTheWordThreshold()
        {
            string[] book =
            [
                "the Commission of Public Safety met",
                .. Enumerable.Repeat("the public safety of the town", 5),
            ];
            Assert.Equal("Commission of Public Safety— . . .",
                ChangeCase("COMMISSION OF PUBLIC SAFETY— . . .", CaseMode.Sentence, book));
            Assert.Equal("The public safety", ChangeCase("THE PUBLIC SAFETY", CaseMode.Sentence, book));
        }

        [Fact]
        public void RestoreForm_IsTheMostFrequentMidSentenceSpelling()
        {
            string[] book = ["they met MacArthur there", "MacArthur's men", "they met MacArthur again", "met Macarthur"];
            Assert.Equal("MacArthur said", ChangeCase("MACARTHUR SAID", CaseMode.Sentence, book));
            Assert.Equal("Ask MacArthur", ChangeCase("ASK MACARTHUR", CaseMode.Sentence, book));
        }

        [Fact]
        public void Possessive_CountsAndRestoresOnTheStem()
        {
            string[] book = ["it was Seldon's plan"];
            Assert.Equal("Seldon's plan", ChangeCase("SELDON'S PLAN", CaseMode.Sentence, book));
            Assert.Equal("The plan was Seldon’s", ChangeCase("THE PLAN WAS SELDON’S", CaseMode.Sentence, book));
        }

        [Fact]
        public void Hyphen_PartsCountAndRestoreOnTheirOwn()
        {
            string[] book = ["a Trantor-born man", "the born leader"];
            Assert.Equal("Trantor-born traders", ChangeCase("TRANTOR-BORN TRADERS", CaseMode.Sentence, book));
            Assert.Equal("Trantor-Born Traders", ChangeCase("TRANTOR-BORN TRADERS", CaseMode.Title, book));
        }

        [Fact]
        public void Title_RestoreFormBeatsTheSmallWordRule()
        {
            string[] book = ["we met By today", "the farm of By was small"];
            Assert.Equal("The Farm of By", ChangeCase("THE FARM OF BY", CaseMode.Title, book));
        }

        [Fact]
        public void SentenceStart_StillUpperCasesALowercaseRestoreForm()
        {
            string[] book = ["he bought an iPhone", "an iPhone again"];
            Assert.Equal("IPhone sales", ChangeCase("IPHONE SALES", CaseMode.Sentence, book));
            Assert.Equal("Buy an iPhone", ChangeCase("BUY AN IPHONE", CaseMode.Sentence, book));
            Assert.Equal("IPhone Cases", ChangeCase("IPHONE CASES", CaseMode.Title, book));
        }

        [Fact]
        public void SingleLetters_AreNeverProtected()
        {
            string[] book = ["then I said A was first", "Plan B worked"];
            Assert.Equal("A plan b", ChangeCase("A PLAN B", CaseMode.Sentence, book));
        }

        [Theory]
        [InlineData(CaseMode.Upper, "HARI SELDON SPOKE")]
        [InlineData(CaseMode.Lower, "hari seldon spoke")]
        public void UpperAndLower_IgnoreTheProtectionSet(CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase("Hari Seldon spoke", null, mode, Foundation));
        }

        private static string ChangeCase(string value, CaseMode mode, IEnumerable<string> book)
            => DeterministicTransformer.ChangeCase(value, null, mode, ProtectionSet.Build(book));
    }
}
