using Read2Me.Services.Characters.ChapterPass;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's answer contract: a GBNF that only admits a roster name or <c>Unknown</c>
    /// plus an optional delivery cue, and the parse that maps the answer back (spec §4.4).
    /// Expected grammars are the lab's (<c>mc.mjs</c> <c>voiceGrammar</c>), written out by hand.
    /// </summary>
    public class RosterGrammarTests
    {
        [Fact]
        public void Two_name_roster_grammar_matches_the_lab()
        {
            var grammar = RosterGrammar.ForRoster(["Pug", "Tomas"]);

            Assert.Equal(
                "root ::= name \" |\" voice\n" +
                "name ::= \"Pug\" | \"Tomas\" | \"Unknown\"\n" +
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
            var grammar = RosterGrammar.ForRoster(["Pug", "unknown", "Pug", "Kulgan"]);

            Assert.Contains("name ::= \"Pug\" | \"Kulgan\" | \"Unknown\"\n", grammar);
        }

        [Fact]
        public void Single_name_grammar_fixes_the_name()
        {
            Assert.Equal(
                "root ::= \"Pug\" \" |\" voice\n" +
                "voice ::= ( \" \" [^\\n|]{1,40} )?",
                RosterGrammar.ForName("Pug"));
        }

        [Theory]
        [InlineData("Pug | angry", "Pug", "angry")]
        [InlineData("Pug |", "Pug", null)]
        [InlineData("Pug |   ", "Pug", null)]
        [InlineData("Pug | soft, hesitant ", "Pug", "soft, hesitant")]
        [InlineData("Pug", "Pug", null)]
        [InlineData("Unknown | shouting", AttributionWire.Unknown, null)]
        [InlineData("Pug | plain", "Pug", null)]
        [InlineData("Pug | Plain ", "Pug", null)]
        [InlineData("Pug | PLAIN", "Pug", null)]
        [InlineData("Pug | plainly annoyed", "Pug", "plainly annoyed")]
        public void Parses_a_name_and_delivery(string raw, string name, string? delivery)
        {
            Assert.True(RosterGrammar.TryParse(raw, ["Pug", "Tomas"], out var n, out var d));
            Assert.Equal(name, n);
            Assert.Equal(delivery, d);
        }

        [Theory]
        [InlineData("Pug | plain", null)]
        [InlineData("Pug | Plain ", null)]
        [InlineData("Pug | plainly annoyed", "plainly annoyed")]
        [InlineData("Pug | dry, amused", "dry, amused")]
        public void A_voice_only_answer_of_plain_has_no_delivery(string raw, string? delivery)
        {
            Assert.Equal(delivery, RosterGrammar.ParseDelivery(raw));
        }

        [Theory]
        [InlineData("Kulgan | calm")]
        [InlineData("pug | calm")]
        [InlineData("")]
        public void A_name_not_on_the_list_does_not_parse(string raw)
        {
            Assert.False(RosterGrammar.TryParse(raw, ["Pug", "Tomas"], out _, out _));
        }
    }
}
