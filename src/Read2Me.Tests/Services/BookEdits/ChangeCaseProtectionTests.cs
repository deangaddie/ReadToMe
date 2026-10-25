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
        private const string AllCapsPattern = EditProgramSchema.AllCapsPattern;

        /// <summary>A book of real Project Gutenberg lines (Dracula, the Sherlock Holmes stories, Alice,
        /// Pride and Prejudice, the Gutenberg licence and a footnote from The Prince): the survey's
        /// all-caps values (which carry no case evidence) plus prose that uses the names mid-sentence
        /// and the ordinary words in lowercase. "red" is below the word threshold and hyphen parts
        /// count on their own, so "Red-headed League" keeps only "League".</summary>
        private static readonly string[] GutenbergBook =
        [
            "CHAPTER IV",
            "“MY DEAR MR. SHERLOCK HOLMES,—You really did it very well. You took me in completely.",
            "MINA MURRAY’S JOURNAL",
            "labelled “ORANGE MARMALADE”, but to her great disappointment it was empty",
            "JONATHAN HARKER’S JOURNAL—continued",
            "“THE RED-HEADED LEAGUE IS DISSOLVED. October 9, 1890.”",
            "Louis XII divorced his wife, Jeanne, daughter of Louis XI, and married in 1499 Anne of Brittany,",
            "widow of Charles VIII, in order to retain the Duchy of Brittany for the crown.",
            "THE FIVE ORANGE PIPS",
            "THE RED-HEADED LEAGUE",
            "THE MAN WITH THE TWISTED LIP",
            "THE ESCAPED WOLF.",
            "PERILOUS ADVENTURE OF OUR INTERVIEWER.",
            "Most people start at our website which has the main PG search facility: www.gutenberg.org.",
            "A HAMPSTEAD MYSTERY.",
            "SIR WILLIAM stayed only a week at Hunsford",
            "YOU AGREE THAT THE FOUNDATION, THE TRADEMARK OWNER, AND ANY DISTRIBUTOR",
            "procession, came THE KING AND QUEEN OF HEARTS.",
            "I leave a photograph which he might care to possess; and I remain, dear Mr. Sherlock Holmes,",
            "“That was Miss Mina Murray?” Again I assented.",
            "he told me of a diary kept by Jonathan Harker when abroad",
            "I was so absorbed in that wonderful diary of Jonathan Harker and that other of his wife",
            "The neighbourhood of Hampstead is just at present exercised with a series of events",
            "It was at Sir William Lucas’s, where a large party were assembled.",
            "I defy even Sir William Lucas himself to produce a more valuable son-in-law.",
            "The King and Queen of Hearts were seated on their throne when they arrived,",
            "“Herald, read the accusation!” said the King.",
            "The Project Gutenberg Literary Archive Foundation (“the Foundation” or PGLAF), owns a compilation copyright",
            "I asked him if he could tell me what had become of the Red-headed League.",
            "Wilson’s assistant counts for a good deal in this mystery of the Red-headed League.",
            "by hearing the very remarkable narrative of the Red-headed League.",
            "All red-headed men who are sound in body and mind and above the age of twenty-one years, are eligible.",
            "Wilson, that I was a red-headed man.",
            "He was himself red-headed, and he had a great sympathy for all red-headed men;",
            "there would be millions of red-headed men who would apply.",
            "Fleet Street was choked with red-headed folk, and Pope’s Court looked like a coster’s orange barrow.",
            "What, the red-headed man?",
            "upon a corner house, announced the place where our red-headed client carried on his business.",
            "there are no red-headed clients to vex us with their conundrums.",
            "from the extraordinary story of the red-headed copier of the Encyclopædia down to the visit to Saxe-Coburg Square",
            "Your red-headed idea was very new and effective.",
        ];

        private static readonly ProtectionSet Gutenberg = ProtectionSet.Build(GutenbergBook);

        [Theory]
        // The survey's 19 sample rows ("Simulation", protected column), as Gutenberg lines of the same shape
        [InlineData("“MY DEAR MR. SHERLOCK HOLMES,—You really did it very well.", "“My dear Mr. Sherlock Holmes,—You really did it very well.")]
        [InlineData("MINA MURRAY’S JOURNAL", "Mina Murray’s journal")]
        [InlineData("“ORANGE MARMALADE”", "“Orange marmalade”")]
        [InlineData("JONATHAN HARKER’S JOURNAL—continued", "Jonathan Harker’s journal—continued")]
        [InlineData("“THE RED-HEADED LEAGUE IS DISSOLVED. October 9, 1890.”", "“The red-headed League is dissolved. October 9, 1890.”")]
        [InlineData("Louis XII divorced his wife, Jeanne", "Louis XII divorced his wife, Jeanne")]
        [InlineData("THE FIVE ORANGE PIPS", "The five orange pips")]
        [InlineData("THE RED-HEADED LEAGUE", "The red-headed League")]
        [InlineData("THE MAN WITH THE TWISTED LIP", "The man with the twisted lip")]
        [InlineData("THE ESCAPED WOLF.", "The escaped wolf.")]
        [InlineData("widow of Charles VIII, in order", "widow of Charles VIII, in order")]
        [InlineData("daughter of Louis XI, and married", "daughter of Louis XI, and married")]
        [InlineData("married in 1499 Anne of Brittany", "married in 1499 Anne of Brittany")]
        [InlineData("the main PG search facility", "the main PG search facility")]
        [InlineData("A HAMPSTEAD MYSTERY.", "A Hampstead mystery.")]
        [InlineData("SIR WILLIAM stayed only a week", "Sir William stayed only a week")]
        [InlineData("YOU AGREE THAT THE FOUNDATION, THE TRADEMARK OWNER", "You agree that the Foundation, the trademark owner")]
        [InlineData("THE KING AND QUEEN OF HEARTS", "The King and Queen of Hearts")]
        [InlineData("CHAPTER IV", "Chapter IV")]
        public void Sentence_SurveyRows(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, AllCapsPattern, CaseMode.Sentence, Gutenberg));
        }

        [Theory]
        [InlineData("THE RED-HEADED LEAGUE", "The Red-Headed League")]
        [InlineData("THE NEIGHBOURHOOD OF HAMPSTEAD", "The Neighbourhood of Hampstead")]
        [InlineData("SHERLOCK HOLMES AND THE FOUNDATION", "Sherlock Holmes and the Foundation")]
        public void Title_KeepsNamesAndSmallWords(string value, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase(value, null, CaseMode.Title, Gutenberg));
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
                "the Committee of Public Safety met",
                .. Enumerable.Repeat("the public safety of the town", 5),
            ];
            Assert.Equal("Committee of Public Safety— . . .",
                ChangeCase("COMMITTEE OF PUBLIC SAFETY— . . .", CaseMode.Sentence, book));
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
            string[] book = ["it was Bingley's letter"];
            Assert.Equal("Bingley's letter", ChangeCase("BINGLEY'S LETTER", CaseMode.Sentence, book));
            Assert.Equal("The letter was Bingley’s", ChangeCase("THE LETTER WAS BINGLEY’S", CaseMode.Sentence, book));
        }

        [Fact]
        public void Hyphen_PartsCountAndRestoreOnTheirOwn()
        {
            string[] book = ["a Longbourn-born man", "the born leader"];
            Assert.Equal("Longbourn-born clerks", ChangeCase("LONGBOURN-BORN CLERKS", CaseMode.Sentence, book));
            Assert.Equal("Longbourn-Born Clerks", ChangeCase("LONGBOURN-BORN CLERKS", CaseMode.Title, book));
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
        [InlineData(CaseMode.Upper, "SHERLOCK HOLMES SPOKE")]
        [InlineData(CaseMode.Lower, "sherlock holmes spoke")]
        public void UpperAndLower_IgnoreTheProtectionSet(CaseMode mode, string expected)
        {
            Assert.Equal(expected, DeterministicTransformer.ChangeCase("Sherlock Holmes spoke", null, mode, Gutenberg));
        }

        private static string ChangeCase(string value, CaseMode mode, IEnumerable<string> book)
            => DeterministicTransformer.ChangeCase(value, null, mode, ProtectionSet.Build(book));
    }
}
