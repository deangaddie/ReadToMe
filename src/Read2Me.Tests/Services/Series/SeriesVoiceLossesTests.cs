using Read2Me.Services.Series;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>What a push or take removes from a book (#222): the Voices the new set lacks, and the positional rules on them.</summary>
    public class SeriesVoiceLossesTests
    {
        private static readonly Guid Doctor = Guid.NewGuid();
        private static readonly Guid Restored = Guid.NewGuid();
        private static readonly Guid Retired = Guid.NewGuid();

        [Fact]
        public void A_book_holding_only_Voices_the_set_keeps_loses_nothing()
        {
            Assert.Null(SeriesVoiceLosses.Of("four", "The Sign of the Four", [Doctor, Retired], [(Doctor, "Army doctor")], [Doctor]));
        }

        [Fact]
        public void The_Voices_the_set_lacks_go_with_the_positional_rules_on_them()
        {
            var loss = SeriesVoiceLosses.Of("four", "The Sign of the Four", [Doctor],
                [(Doctor, "Army doctor"), (Restored, "Restored"), (Retired, "Retired")],
                [Doctor, Restored, Restored]);

            Assert.NotNull(loss);
            Assert.Equal(["Restored", "Retired"], loss.Voices);
            Assert.Equal(2, loss.RuleCount);
            Assert.Equal(("four", "The Sign of the Four"), (loss.Folder, loss.Title));
        }
    }
}
