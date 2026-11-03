using Read2Me.Services.Series;
using Xunit;
using static Read2Me.Services.Series.SeriesNameMatch;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// Link suggestion matching (#212, context/series.md "Link suggestion"), over the sample series
    /// (#209): A Study in Scarlet, The Sign of the Four and The Adventures of Sherlock Holmes. Two
    /// names match on the same set of non-honorific words, honorifics agreeing where both carry them;
    /// suggestions are one-to-one only, and the seed Narrator row matches by its flag.
    /// </summary>
    public class SeriesNameMatchTests
    {
        private static readonly Guid Scarlet = Guid.NewGuid();
        private static readonly Guid Four = Guid.NewGuid();
        private static readonly Guid Adventures = Guid.NewGuid();

        private static Character Person(string name, params string[] aliases) =>
            new(Guid.NewGuid(), name, aliases, IsNarrator: false, SeriesCharacterId: null);

        private static Character Narrator() => new(Guid.NewGuid(), "Narrator", [], IsNarrator: true, SeriesCharacterId: null);

        private static Character Linked(Character c, Guid seriesCharacterId) => c with { SeriesCharacterId = seriesCharacterId };

        private static Suggestions Suggest(params Book[] books) => SeriesNameMatch.Suggest(books, NotTheSame.None);

        // ---- forms ------------------------------------------------------------------------------

        [Fact]
        public void Sherlock_Holmes_matches_Holmes_comma_Sherlock()
        {
            Assert.True(Matches(["Sherlock Holmes"], ["Holmes, Sherlock"]));
        }

        [Fact]
        public void Case_and_punctuation_are_ignored_but_word_internal_apostrophes_and_hyphens_are_kept()
        {
            Assert.True(Matches(["MR. SHERLOCK HOLMES!"], ["Mr Sherlock Holmes"]));
            Assert.True(Matches(["Jefferson Hope's"], ["Jefferson Hope’s"]));
            Assert.False(Matches(["Lestrade-Gregson"], ["Lestrade Gregson"]));
        }

        [Fact]
        public void Containment_is_rejected_so_John_does_not_match_John_Watson()
        {
            Assert.False(Matches(["John"], ["John Watson"]));
            Assert.False(Matches(["Holmes"], ["Sherlock Holmes"]));
        }

        [Fact]
        public void Mr_Hudson_does_not_match_Mrs_Hudson_but_an_honorific_on_one_side_only_is_set_aside()
        {
            Assert.False(Matches(["Mr. Hudson"], ["Mrs. Hudson"]));
            Assert.True(Matches(["Mrs. Hudson"], ["Hudson"]));
            Assert.True(Matches(["Dr. Watson"], ["Doctor Watson"]));
            Assert.True(Matches(["Madame Forrester"], ["Madam Forrester"]));
            Assert.False(Matches(["Dr. Watson"], ["Mr. Watson"]));
        }

        [Fact]
        public void An_all_lowercase_noun_alias_and_an_honorific_only_form_never_take_part()
        {
            Assert.False(Matches(["the doctor"], ["the doctor"]));
            Assert.False(Matches(["Doctor"], ["Doctor"]));
            Assert.True(Matches(["the doctor", "Watson"], ["John Watson", "Watson"]));
        }

        [Fact]
        public void One_form_pair_is_enough_alias_or_name()
        {
            Assert.True(Matches(["John Watson", "Watson"], ["Dr. Watson"]));
        }

        // ---- suggested cells --------------------------------------------------------------------

        [Fact]
        public void An_unlinked_Character_matching_one_Series_character_one_to_one_is_a_suggested_cell()
        {
            var holmes = Guid.NewGuid();
            var scarletHolmes = Linked(Person("Sherlock Holmes"), holmes);
            var fourHolmes = Person("Holmes, Sherlock");

            var suggestions = Suggest(
                new Book(Scarlet, false, [scarletHolmes, Person("Jefferson Hope")]),
                new Book(Four, false, [fourHolmes, Person("Jonathan Small")]));

            var cell = Assert.Single(suggestions.Cells);
            Assert.Equal(new SuggestedCell(holmes, Four, fourHolmes.CharacterId), cell);
            Assert.Equal([holmes], suggestions.NameMatches[fourHolmes.CharacterId]);
            Assert.Empty(suggestions.Rows);
            Assert.Equal(1, suggestions.Count);
        }

        [Fact]
        public void A_Series_characters_side_is_every_form_of_every_Character_linked_to_it()
        {
            var watson = Guid.NewGuid();
            var fourWatson = Person("Dr. Watson");

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Person("John Watson"), watson)]),
                new Book(Adventures, false, [Linked(Person("Doctor", "Watson"), watson)]),
                new Book(Four, false, [fourWatson]));

            Assert.Equal(new SuggestedCell(watson, Four, fourWatson.CharacterId), Assert.Single(suggestions.Cells));
        }

        [Fact]
        public void An_ambiguous_match_gives_no_suggestion_and_the_picker_lists_the_matches()
        {
            var holmes = Guid.NewGuid();
            var sherlock = Person("Sherlock Holmes");
            var mister = Person("Mr. Sherlock Holmes");

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Person("Sherlock Holmes"), holmes)]),
                new Book(Four, false, [sherlock, mister]));

            Assert.Empty(suggestions.Cells);
            Assert.Empty(suggestions.Rows);
            Assert.Equal([holmes], suggestions.NameMatches[sherlock.CharacterId]);
            Assert.Equal([holmes], suggestions.NameMatches[mister.CharacterId]);
        }

        [Fact]
        public void A_Character_matching_two_Series_characters_gives_no_suggestion()
        {
            var watson = Guid.NewGuid();
            var otherWatson = Guid.NewGuid();
            var fourWatson = Person("Watson");

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Person("Watson"), watson)]),
                new Book(Adventures, false, [Linked(Person("Dr. Watson"), otherWatson)]),
                new Book(Four, false, [fourWatson]));

            Assert.Empty(suggestions.Cells);
            Assert.Equal(2, suggestions.NameMatches[fourWatson.CharacterId].Count);
        }

        [Fact]
        public void A_book_that_already_links_the_Series_character_gets_no_suggestion_for_it()
        {
            var holmes = Guid.NewGuid();
            var stray = Person("Holmes, Sherlock");

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Person("Sherlock Holmes"), holmes)]),
                new Book(Four, false, [Linked(Person("Sherlock Holmes"), holmes), stray]));

            Assert.Empty(suggestions.Cells);
            Assert.False(suggestions.NameMatches.ContainsKey(stray.CharacterId));
        }

        [Fact]
        public void A_dismissed_cell_is_not_suggested_but_its_name_match_still_orders_the_picker()
        {
            var holmes = Guid.NewGuid();
            var fourHolmes = Person("Sherlock Holmes");

            var suggestions = SeriesNameMatch.Suggest(
                [
                    new Book(Scarlet, false, [Linked(Person("Sherlock Holmes"), holmes)]),
                    new Book(Four, false, [fourHolmes]),
                ],
                new NotTheSame([new DismissedCell(holmes, Four, fourHolmes.CharacterId)], []));

            Assert.Empty(suggestions.Cells);
            Assert.Equal([holmes], suggestions.NameMatches[fourHolmes.CharacterId]);
        }

        // ---- the Narrator -----------------------------------------------------------------------

        [Fact]
        public void The_seed_Narrator_row_matches_by_its_flag_never_by_name()
        {
            var narration = Guid.NewGuid();
            var fourNarrator = Narrator();
            var calledNarrator = Person("Narrator");

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Narrator(), narration)]),
                new Book(Four, false, [fourNarrator, calledNarrator]));

            Assert.Equal(new SuggestedCell(narration, Four, fourNarrator.CharacterId), Assert.Single(suggestions.Cells));
            Assert.False(suggestions.NameMatches.ContainsKey(calledNarrator.CharacterId));
        }

        [Fact]
        public void The_Narrator_is_not_suggested_in_a_book_whose_narrator_link_is_set()
        {
            var narration = Guid.NewGuid();

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Narrator(), narration)]),
                new Book(Four, NarratorLinked: true, [Narrator()]));

            Assert.Empty(suggestions.Cells);
        }

        // ---- suggested rows ---------------------------------------------------------------------

        [Fact]
        public void Unlinked_Characters_matching_one_to_one_in_two_or_more_books_are_a_suggested_row()
        {
            var scarletWatson = Person("John Watson", "Watson");
            var fourWatson = Person("Dr. Watson");
            var adventuresWatson = Person("Watson", "the doctor");

            var suggestions = Suggest(
                new Book(Scarlet, false, [scarletWatson, Person("Jefferson Hope")]),
                new Book(Four, false, [fourWatson, Person("Mary Morstan")]),
                new Book(Adventures, false, [adventuresWatson, Person("Irene Adler")]));

            var row = Assert.Single(suggestions.Rows);
            Assert.Equal(
                [new Member(Scarlet, scarletWatson.CharacterId), new Member(Four, fourWatson.CharacterId), new Member(Adventures, adventuresWatson.CharacterId)],
                row);
            Assert.Empty(suggestions.Cells);
            Assert.Equal(1, suggestions.Count);
        }

        [Fact]
        public void The_seed_Narrator_rows_of_two_books_are_a_suggested_row()
        {
            var scarletNarrator = Narrator();
            var fourNarrator = Narrator();

            var suggestions = Suggest(
                new Book(Scarlet, false, [scarletNarrator]),
                new Book(Four, false, [fourNarrator]),
                new Book(Adventures, NarratorLinked: true, [Narrator()]));

            Assert.Equal([new Member(Scarlet, scarletNarrator.CharacterId), new Member(Four, fourNarrator.CharacterId)], Assert.Single(suggestions.Rows));
        }

        [Fact]
        public void An_ambiguous_match_between_books_makes_no_suggested_row()
        {
            var suggestions = Suggest(
                new Book(Scarlet, false, [Person("John Rance")]),
                new Book(Four, false, [Person("John Rance"), Person("Rance, John")]));

            Assert.Empty(suggestions.Rows);
        }

        [Fact]
        public void A_dismissed_pair_breaks_the_row_between_those_two_books()
        {
            var scarletWatson = Person("Watson");
            var fourWatson = Person("Watson");

            var suggestions = SeriesNameMatch.Suggest(
                [new Book(Scarlet, false, [scarletWatson]), new Book(Four, false, [fourWatson])],
                new NotTheSame([], [new DismissedPair(new Member(Four, fourWatson.CharacterId), new Member(Scarlet, scarletWatson.CharacterId))]));

            Assert.Empty(suggestions.Rows);
        }

        [Fact]
        public void A_Character_that_name_matches_a_Series_character_is_not_also_in_a_suggested_row()
        {
            var holmes = Guid.NewGuid();
            var fourHolmes = Person("Sherlock Holmes");

            var suggestions = Suggest(
                new Book(Scarlet, false, [Linked(Person("Sherlock Holmes"), holmes)]),
                new Book(Four, false, [fourHolmes]),
                new Book(Adventures, false, [Person("Sherlock Holmes")]));

            Assert.Equal(2, suggestions.Cells.Count);
            Assert.Empty(suggestions.Rows);
        }
    }
}
