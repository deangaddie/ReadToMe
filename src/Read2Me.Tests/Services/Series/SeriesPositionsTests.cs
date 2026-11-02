using Read2Me.Services.Series;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// The Series position rules (#206): 0 or more with at most two decimals, the next whole number
    /// for a joining book, and a whole set checked together against the members.
    /// </summary>
    public class SeriesPositionsTests
    {
        [Theory]
        [InlineData("0")]
        [InlineData("1")]
        [InlineData("1.5")]
        [InlineData("2.25")]
        [InlineData("2.50")]
        [InlineData("12.000")]
        public void A_position_of_zero_or_more_with_at_most_two_decimals_is_well_formed(string value) =>
            Assert.True(SeriesPositions.IsWellFormed(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

        [Theory]
        [InlineData("-1")]
        [InlineData("-0.5")]
        [InlineData("1.125")]
        [InlineData("0.001")]
        public void A_negative_position_or_one_with_more_than_two_decimals_is_malformed(string value) =>
            Assert.False(SeriesPositions.IsWellFormed(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

        [Fact]
        public void A_book_joining_an_empty_series_is_Book_1() =>
            Assert.Equal(1m, SeriesPositions.Next([]));

        [Fact]
        public void A_joining_book_gets_the_next_whole_number_after_the_highest()
        {
            Assert.Equal(3m, SeriesPositions.Next([1m, 2.5m]));
            Assert.Equal(4m, SeriesPositions.Next([3m, 0m]));
            Assert.Equal(1m, SeriesPositions.Next([0m]));
            Assert.Equal(8m, SeriesPositions.Next([7.99m, 2m]));
        }

        [Fact]
        public void Positions_are_shown_without_trailing_zeros() =>
            Assert.Equal("2.5", SeriesPositions.Normalize(2.50m).ToString(System.Globalization.CultureInfo.InvariantCulture));

        [Fact]
        public void A_set_covering_exactly_the_members_with_distinct_well_formed_positions_is_accepted()
        {
            var check = SeriesPositions.CheckSet(
                ["scarlet", "four"],
                new Dictionary<string, decimal> { ["four"] = 1m, ["scarlet"] = 2m });

            Assert.IsType<SeriesPositionCheck.Accepted>(check);
        }

        [Fact]
        public void A_swap_of_two_books_is_not_a_tie_because_the_set_is_checked_together()
        {
            var check = SeriesPositions.CheckSet(
                ["scarlet", "four", "adventures"],
                new Dictionary<string, decimal> { ["scarlet"] = 2m, ["four"] = 1m, ["adventures"] = 3m });

            Assert.IsType<SeriesPositionCheck.Accepted>(check);
        }

        [Fact]
        public void A_malformed_position_in_the_set_names_its_book()
        {
            var check = SeriesPositions.CheckSet(
                ["scarlet", "four"],
                new Dictionary<string, decimal> { ["scarlet"] = 1m, ["four"] = 1.505m });

            var malformed = Assert.IsType<SeriesPositionCheck.Malformed>(check);
            Assert.Equal("four", malformed.Book);
        }

        [Fact]
        public void A_set_missing_a_member_is_out_of_date() =>
            Assert.IsType<SeriesPositionCheck.OutOfDate>(SeriesPositions.CheckSet(
                ["scarlet", "four"],
                new Dictionary<string, decimal> { ["scarlet"] = 1m }));

        [Fact]
        public void A_set_naming_a_book_outside_the_series_is_out_of_date() =>
            Assert.IsType<SeriesPositionCheck.OutOfDate>(SeriesPositions.CheckSet(
                ["scarlet"],
                new Dictionary<string, decimal> { ["scarlet"] = 1m, ["four"] = 2m }));

        [Fact]
        public void Members_match_set_keys_ignoring_case_as_folders_do() =>
            Assert.IsType<SeriesPositionCheck.Accepted>(SeriesPositions.CheckSet(
                ["Scarlet"],
                new Dictionary<string, decimal> { ["scarlet"] = 1m }));

        [Fact]
        public void Two_books_on_one_number_is_a_tie_naming_both()
        {
            var check = SeriesPositions.CheckSet(
                ["scarlet", "four", "adventures"],
                new Dictionary<string, decimal> { ["scarlet"] = 1m, ["four"] = 2m, ["adventures"] = 2.00m });

            var tie = Assert.IsType<SeriesPositionCheck.Tie>(check);
            Assert.Equal(2m, tie.Position);
            Assert.Equal(["adventures", "four"], new[] { tie.First, tie.Second }.Order());
        }

        [Fact]
        public void A_malformed_position_is_reported_before_a_stale_set()
        {
            var check = SeriesPositions.CheckSet(
                ["scarlet", "four"],
                new Dictionary<string, decimal> { ["scarlet"] = -1m });

            Assert.IsType<SeriesPositionCheck.Malformed>(check);
        }
    }
}
