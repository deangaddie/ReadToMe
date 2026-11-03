using Read2Me.Services.Series;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// The Voice merge on link (ADR 0018, Character linking #208): each side gains the other's
    /// Voices, matched by Voice Id, so a Voice both hold is never copied.
    /// </summary>
    public class SeriesVoiceMergeTests
    {
        private sealed record V(Guid Id, string Name);

        private static readonly V Deerstalker = new(Guid.NewGuid(), "Deerstalker");
        private static readonly V Baker = new(Guid.NewGuid(), "Baker Street");
        private static readonly V Violin = new(Guid.NewGuid(), "Violin");

        private static SeriesVoiceMerge.Plan<V> Plan(IReadOnlyList<V> joining, params (string Folder, V[] Voices)[] linked) =>
            SeriesVoiceMerge.Make(joining, linked.Select(l => new SeriesVoiceMerge.BookVoices<V>(l.Folder, l.Voices)).ToList(), v => v.Id);

        [Fact]
        public void Each_side_gains_the_Voices_it_lacks_and_the_one_both_hold_is_left_alone()
        {
            var plan = Plan([Baker, Violin], ("scarlet", [Deerstalker, Baker]));

            Assert.Equal([Deerstalker], plan.JoiningGains.Select(g => g.Voice));
            Assert.Equal(["scarlet"], plan.JoiningGains.Select(g => g.FromFolder));
            var scarlet = Assert.Single(plan.LinkedGains);
            Assert.Equal("scarlet", scarlet.Folder);
            Assert.Equal([Violin], scarlet.Voices);
        }

        [Fact]
        public void A_Voice_held_by_several_linked_books_is_gained_once_from_the_lowest_positioned()
        {
            var plan = Plan([], ("scarlet", [Deerstalker]), ("adventures", [Deerstalker, Baker]));

            Assert.Equal([(Deerstalker, "scarlet"), (Baker, "adventures")],
                plan.JoiningGains.Select(g => (g.Voice, g.FromFolder)));
            Assert.Empty(plan.LinkedGains);
        }

        [Fact]
        public void Every_linked_book_gains_what_the_joining_Character_holds_and_it_lacks()
        {
            var plan = Plan([Violin, Baker], ("scarlet", [Baker]), ("adventures", []));

            Assert.Equal(["scarlet", "adventures"], plan.LinkedGains.Select(b => b.Folder));
            Assert.Equal([Violin], plan.LinkedGains[0].Voices);
            Assert.Equal([Violin, Baker], plan.LinkedGains[1].Voices);
        }

        [Fact]
        public void Sharing_as_new_gains_nothing_on_either_side()
        {
            var plan = Plan([Violin]);

            Assert.Empty(plan.JoiningGains);
            Assert.Empty(plan.LinkedGains);
        }
    }
}
