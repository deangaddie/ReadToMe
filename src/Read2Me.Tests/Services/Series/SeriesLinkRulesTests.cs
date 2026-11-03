using Read2Me.Services.Series;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// Who may link to a Series character (ADR 0018, context/series.md "Linked character"): a book
    /// links at most one Character to a given Series character, and a Character is linked to at most
    /// one. The seed Narrator row is a Character like any other here.
    /// </summary>
    public class SeriesLinkRulesTests
    {
        private static readonly Guid Holmes = Guid.NewGuid();
        private static readonly Guid Narration = Guid.NewGuid();
        private static readonly Guid Scarlet = Guid.NewGuid();
        private static readonly Guid Four = Guid.NewGuid();
        private static readonly Guid ScarletHolmes = Guid.NewGuid();
        private static readonly Guid FourHolmes = Guid.NewGuid();
        private static readonly Guid FourSherlock = Guid.NewGuid();
        private static readonly Guid ScarletNarrator = Guid.NewGuid();
        private static readonly Guid FourNarrator = Guid.NewGuid();

        private static readonly SeriesLinkRules.Link[] Links =
        [
            new(Holmes, Scarlet, ScarletHolmes),
            new(Narration, Scarlet, ScarletNarrator),
        ];

        [Fact]
        public void An_unlinked_Character_of_a_book_with_no_link_to_that_Series_character_may_link()
        {
            Assert.IsType<SeriesLinkRules.Allowed>(SeriesLinkRules.Check(Links, Holmes, Four, FourHolmes));
        }

        [Fact]
        public void A_second_Character_of_the_same_book_is_refused_naming_the_one_already_linked()
        {
            var links = Links.Append(new SeriesLinkRules.Link(Holmes, Four, FourHolmes)).ToArray();

            var refused = Assert.IsType<SeriesLinkRules.BookAlreadyLinked>(SeriesLinkRules.Check(links, Holmes, Four, FourSherlock));
            Assert.Equal(FourHolmes, refused.CharacterId);
        }

        [Fact]
        public void A_Character_already_linked_is_refused_naming_its_Series_character_even_the_same_one()
        {
            var elsewhere = Assert.IsType<SeriesLinkRules.CharacterAlreadyLinked>(
                SeriesLinkRules.Check(Links, Narration, Scarlet, ScarletHolmes));
            Assert.Equal(Holmes, elsewhere.SeriesCharacterId);

            var again = Assert.IsType<SeriesLinkRules.CharacterAlreadyLinked>(
                SeriesLinkRules.Check(Links, Holmes, Scarlet, ScarletHolmes));
            Assert.Equal(Holmes, again.SeriesCharacterId);
        }

        [Fact]
        public void The_Narrator_row_links_like_any_other_Character()
        {
            Assert.IsType<SeriesLinkRules.Allowed>(SeriesLinkRules.Check(Links, Narration, Four, FourNarrator));
        }

        [Fact]
        public void Sharing_as_new_is_refused_only_for_a_Character_already_linked()
        {
            Assert.Null(SeriesLinkRules.CheckShare(Links, Four, FourHolmes));
            Assert.Equal(Holmes, SeriesLinkRules.CheckShare(Links, Scarlet, ScarletHolmes)?.SeriesCharacterId);
        }

        [Fact]
        public void Removing_a_books_links_names_the_Series_characters_left_with_none()
        {
            var links = Links.Append(new SeriesLinkRules.Link(Holmes, Four, FourHolmes)).ToArray();

            Assert.Equal([Narration], SeriesLinkRules.OrphanedBy(links, l => l.ProjectId == Scarlet));
        }
    }
}
