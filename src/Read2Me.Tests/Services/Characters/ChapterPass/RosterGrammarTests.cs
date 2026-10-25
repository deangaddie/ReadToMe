using Read2Me.Services.Characters.ChapterPass;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's answer contract: a GBNF that only admits a roster name or <c>Unknown</c>
    /// plus an optional delivery cue, and the parse that maps the answer back.
    /// Expected grammars are the measured prototype's, written out by hand.
    /// </summary>
    public class RosterGrammarTests
    {
        [Fact]
        public void Two_name_roster_grammar_matches_the_prototype()
        {
            var grammar = RosterGrammar.ForRoster(["Holmes", "Lestrade"]);

            Assert.Equal(
                "root ::= name \" |\" voice\n" +
                "name ::= \"Holmes\" | \"Lestrade\" | \"Unknown\"\n" +
                "voice ::= ( \" \" [^\\n|]{1,40} )?",
                grammar);
        }

        [Fact]
        public void Quotes_and_backslashes_are_escaped()
        {
            var grammar = RosterGrammar.ForRoster(["Mr. \"Q\"", "Back\\slash"]);

            Assert.Contains("name ::= \"Mr. \\\"Q\\\"\" | \"Back\\\\slash\" | \"Unknown\"\n", grammar);
        }

        [Fact]
        public void Duplicates_collapse_and_a_roster_name_unknown_is_dropped()
        {
            var grammar = RosterGrammar.ForRoster(["Holmes", "unknown", "Holmes", "Watson"]);

            Assert.Contains("name ::= \"Holmes\" | \"Watson\" | \"Unknown\"\n", grammar);
        }

        [Fact]
        public void Single_name_grammar_fixes_the_name()
        {
            Assert.Equal(
                "root ::= \"Holmes\" \" |\" voice\n" +
                "voice ::= ( \" \" [^\\n|]{1,40} )?",
                RosterGrammar.ForName("Holmes"));
        }

        [Theory]
        [InlineData("Holmes | angry", "Holmes", "angry")]
        [InlineData("Holmes |", "Holmes", null)]
        [InlineData("Holmes |   ", "Holmes", null)]
        [InlineData("Holmes | soft, hesitant ", "Holmes", "soft, hesitant")]
        [InlineData("Holmes", "Holmes", null)]
        [InlineData("Unknown | shouting", AttributionWire.Unknown, null)]
        [InlineData("Holmes | plain", "Holmes", null)]
        [InlineData("Holmes | Plain ", "Holmes", null)]
        [InlineData("Holmes | PLAIN", "Holmes", null)]
        [InlineData("Holmes | plainly annoyed", "Holmes", "plainly annoyed")]
        public void Parses_a_name_and_delivery(string raw, string name, string? delivery)
        {
            Assert.True(RosterGrammar.TryParse(raw, ["Holmes", "Lestrade"], out var n, out var d));
            Assert.Equal(name, n);
            Assert.Equal(delivery, d);
        }

        [Theory]
        [InlineData("Holmes | plain", null)]
        [InlineData("Holmes | Plain ", null)]
        [InlineData("Holmes | plainly annoyed", "plainly annoyed")]
        [InlineData("Holmes | dry, amused", "dry, amused")]
        public void A_voice_only_answer_of_plain_has_no_delivery(string raw, string? delivery)
        {
            Assert.Equal(delivery, RosterGrammar.ParseDelivery(raw));
        }

        [Theory]
        [InlineData("Watson | calm")]
        [InlineData("holmes | calm")]
        [InlineData("")]
        public void A_name_not_on_the_list_does_not_parse(string raw)
        {
            Assert.False(RosterGrammar.TryParse(raw, ["Holmes", "Lestrade"], out _, out _));
        }
    }
}
