using Read2Me.Services;
using Read2Me.Services.Characters.Rules;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters.Rules
{
    /// <summary>
    /// The zero-LLM speaker tagger (port of the measured prototype's <c>tagChapter</c>): one
    /// synthetic paragraph per tier and per guard. A test names the tag it expects, or that the item
    /// is left to the model.
    /// </summary>
    public class SpeechTagRulesTests
    {
        private static readonly IReadOnlyList<RosterEntry> Roster =
        [
            new("Pug", []),
            new("Kulgan", ["the magician"]),
            new("Tomas", ["Tom"]),
            new("Carline", []),
            new("Lord Borric", []),
        ];

        private static ContextItem N(string text) =>
            new(Guid.NewGuid(), text, AttributionWire.Narration, AttributionWire.Narrator);

        private static ContextItem D(string text) =>
            new(Guid.NewGuid(), text, AttributionWire.Dialog, AttributionWire.Unknown);

        private static ChapterParagraph P(params ContextItem[] items) => new(Guid.NewGuid(), items);

        private static IReadOnlyDictionary<Guid, RuleTag> Tag(
            IReadOnlyList<RosterEntry>? roster = null, params ChapterParagraph[] chapter) =>
            SpeechTagRules.TagChapter(chapter, roster ?? Roster);

        private static RuleTag? TagOf(IReadOnlyDictionary<Guid, RuleTag> tags, ContextItem item) =>
            tags.TryGetValue(item.ItemId, out var tag) ? tag : null;

        [Fact]
        public void T1_post_tag_with_the_verb_first_names_the_speaker()
        {
            var quote = D("“We must go,”");
            var tags = Tag(null, P(quote, N("said Pug.")));

            Assert.Equal(new RuleTag("Pug", "T1-post-vs"), TagOf(tags, quote));
        }

        [Fact]
        public void T1_post_tag_with_the_subject_first_names_the_speaker()
        {
            var quote = D("“We must go,”");
            var tags = Tag(null, P(quote, N("Pug said.")));

            Assert.Equal(new RuleTag("Pug", "T1-post-sv"), TagOf(tags, quote));
        }

        [Fact]
        public void T1_pre_tag_names_the_speaker()
        {
            var quote = D("“Sit down.”");
            var tags = Tag(null, P(N("Kulgan said,"), quote));

            Assert.Equal(new RuleTag("Kulgan", "T1-pre"), TagOf(tags, quote));
        }

        [Fact]
        public void Narration_items_are_never_tagged()
        {
            var narration = N("said Pug.");
            var tags = Tag(null, P(D("“We must go,”"), narration));

            Assert.Null(TagOf(tags, narration));
            Assert.Single(tags);
        }

        [Fact]
        public void T3_pronoun_pre_tag_resolves_to_the_subject_of_the_sentence_before()
        {
            var quote = D("“Listen.”");
            var tags = Tag(null, P(N("Kulgan lit his pipe. He said,"), quote));

            Assert.Equal(new RuleTag("Kulgan", "T3-pron-pre"), TagOf(tags, quote));
        }

        [Fact]
        public void T3_pronoun_post_tag_resolves_within_the_paragraph()
        {
            var quote = D("“Come,”");
            var tags = Tag(null, P(N("Kulgan smiled."), quote, N("he said.")));

            Assert.Equal(new RuleTag("Kulgan", "T3-pron-post-sv"), TagOf(tags, quote));
        }

        [Fact]
        public void Gender_guard_an_antecedent_sentence_with_an_opposite_pronoun_does_not_resolve()
        {
            var quote = D("“Listen.”");
            var tags = Tag(null, P(N("Kulgan looked at her. He said,"), quote));

            Assert.Null(TagOf(tags, quote));
        }

        [Fact]
        public void T3_never_crosses_into_the_previous_paragraph()
        {
            var quote = D("“Come,”");
            var tags = Tag(null, P(N("Kulgan smiled.")), P(quote, N("he said.")));

            Assert.Null(TagOf(tags, quote));
        }

        [Fact]
        public void T4_an_action_beat_before_the_first_quote_names_the_speaker()
        {
            var quote = D("“I am ready.”");
            var tags = Tag(null, P(N("Pug stood up."), quote));

            Assert.Equal(new RuleTag("Pug", "T4-beat"), TagOf(tags, quote));
        }

        [Fact]
        public void Mid_beat_a_beat_before_a_later_quote_is_never_an_answer()
        {
            var second = D("“I am ready.”");
            var tags = Tag(null, P(D("“Yes.”"), N("Pug stood up."), second));

            Assert.Empty(tags);
            Assert.Null(TagOf(tags, second));
        }

        [Fact]
        public void T2_propagates_the_single_paragraph_speaker_to_an_untagged_quote()
        {
            var first = D("“We must go,”");
            var second = D("“The storm is coming.”");
            var tags = Tag(null, P(first, N("said Pug."), second));

            Assert.Equal(new RuleTag("Pug", "T1-post-vs"), TagOf(tags, first));
            Assert.Equal(new RuleTag("Pug", "T2-prop"), TagOf(tags, second));
        }

        [Fact]
        public void Embedded_quote_guard_a_quoted_phrase_mid_sentence_is_not_a_turn()
        {
            var phrase = D("“Galacian Girls”");
            var tags = Tag(null, P(D("“Look,”"), N("said Pug, who was humming"), phrase, N("all night.")));

            Assert.Null(TagOf(tags, phrase));
        }

        [Fact]
        public void Mid_beat_guard_another_character_acting_between_quotes_blocks_propagation()
        {
            var second = D("“Now.”");
            var tags = Tag(null, P(D("“Go,”"), N("said Pug. Kulgan frowned."), second));

            Assert.Null(TagOf(tags, second));
        }

        [Fact]
        public void Back_guard_propagation_back_only_crosses_a_post_tag_fragment()
        {
            var before = D("“What shall I do?”");
            var tags = Tag(null, P(before, N("The room was dark."), D("“Wait,”"), N("said Pug.")));

            Assert.Null(TagOf(tags, before));
        }

        [Fact]
        public void Person_clash_guard_an_unresolved_first_person_tag_blocks_propagation()
        {
            var second = D("“No,”");
            var tags = Tag(null, P(D("“Go,”"), N("said Pug."), second, N("I said.")));

            Assert.Null(TagOf(tags, second));
        }

        [Fact]
        public void T5_fills_an_unbroken_two_person_exchange_between_anchors()
        {
            var p1 = D("“To the keep.”");
            var p2 = D("“Why?”");
            var tags = Tag(null,
                P(D("“Where are we going?”"), N("asked Pug.")),
                P(p1),
                P(p2),
                P(D("“Because the duke asked,”"), N("said Kulgan.")));

            Assert.Equal(new RuleTag("Kulgan", "T5-alt"), TagOf(tags, p1));
            Assert.Equal(new RuleTag("Pug", "T5-alt"), TagOf(tags, p2));
        }

        [Fact]
        public void T5_does_not_fill_across_a_narration_paragraph()
        {
            var p2 = D("“Why?”");
            var tags = Tag(null,
                P(D("“Where are we going?”"), N("asked Pug.")),
                P(N("The wind rose.")),
                P(D("“To the keep.”")),
                P(p2),
                P(D("“Because the duke asked,”"), N("said Kulgan.")));

            Assert.Null(TagOf(tags, p2));
        }

        [Fact]
        public void Reported_speech_is_not_a_tag()
        {
            var quote = D("“Fine,”");
            var tags = Tag(null, P(quote, N("Pug said he would come.")));

            Assert.Null(TagOf(tags, quote));
        }

        [Fact]
        public void A_compound_subject_is_unresolved()
        {
            var pre = D("“Yes.”");
            var post = D("“Yes,”");
            var tags = Tag(null, P(N("Pug and Kulgan said,"), pre), P(post, N("said Pug and Kulgan.")));

            Assert.Null(TagOf(tags, pre));
            Assert.Null(TagOf(tags, post));
        }

        [Fact]
        public void An_alias_resolves_to_its_character()
        {
            var noun = D("“Sit,”");
            var name = D("“Hurry,”");
            var tags = Tag(null, P(noun, N("said the magician.")), P(name, N("said Tom.")));

            Assert.Equal(new RuleTag("Kulgan", "T1-post-vs"), TagOf(tags, noun));
            Assert.Equal(new RuleTag("Tomas", "T1-post-vs"), TagOf(tags, name));
        }

        [Fact]
        public void An_honorific_alone_gives_no_tag()
        {
            var quote = D("“Halt,”");
            var tags = Tag(null, P(quote, N("said Lord.")));

            Assert.Null(TagOf(tags, quote));
        }

        [Fact]
        public void A_name_shared_by_two_characters_gives_no_tag_and_the_honorific_picks_one()
        {
            IReadOnlyList<RosterEntry> bennets = [new("Mr. Bennet", []), new("Mrs. Bennet", [])];
            var bare = D("“Indeed,”");
            var titled = D("“Indeed,”");
            var tags = Tag(bennets, P(bare, N("said Bennet.")), P(titled, N("said Mrs. Bennet.")));

            Assert.Null(TagOf(tags, bare));
            Assert.Equal(new RuleTag("Mrs. Bennet", "T1-post-vs"), TagOf(tags, titled));
        }

        [Fact]
        public void A_speaker_not_on_the_roster_blocks_the_quote()
        {
            var first = D("“Run,”");
            var second = D("“Now.”");
            var tags = Tag(null, P(first, N("said Laurie."), second));

            Assert.Empty(tags);
        }

        [Fact]
        public void Tiers_can_be_turned_off()
        {
            var first = D("“We must go,”");
            var second = D("“The storm is coming.”");
            var tags = SpeechTagRules.TagChapter([P(first, N("said Pug."), second)], Roster, new RuleTiers(T2: false));

            Assert.Equal(new RuleTag("Pug", "T1-post-vs"), TagOf(tags, first));
            Assert.Null(TagOf(tags, second));
        }

        // ---- DiscoverNames: capitalised tag names the roster does not know

        private static readonly IReadOnlyList<RosterEntry> DiscoverRoster =
        [
            new("Pug", []),
            new("Kulgan", ["the magician"]),
            new("Duke Borric", []),
        ];

        private static IReadOnlyList<DiscoveredName> Discover(params ChapterParagraph[] chapter) =>
            SpeechTagRules.DiscoverNames(chapter, DiscoverRoster);

        [Fact]
        public void Discover_finds_a_said_tag_name_missing_from_the_roster()
        {
            var found = Discover(P(D("“We must go,”"), N("said Laurie.")));

            Assert.Equal([new DiscoveredName("Laurie", 1, "said Laurie.")], found);
        }

        [Fact]
        public void Discover_drops_a_leading_ly_adverb()
        {
            var found = Discover(P(D("“We must go,”"), N("said Suddenly Laurie.")));

            Assert.Equal("Laurie", Assert.Single(found).Name);
        }

        [Fact]
        public void Discover_drops_a_leading_opener()
        {
            var found = Discover(P(D("“We must go,”"), N("said Well Laurie.")));

            Assert.Equal("Laurie", Assert.Single(found).Name);
        }

        [Fact]
        public void Discover_ignores_an_honorific_alone()
        {
            Assert.Empty(Discover(P(D("“We must go,”"), N("said Lord."))));
        }

        [Fact]
        public void Discover_ignores_a_mention_sharing_a_word_with_a_roster_name()
        {
            Assert.Empty(Discover(P(D("“We must go,”"), N("said Lord Borric."))));
        }

        [Fact]
        public void Discover_ignores_a_pronoun()
        {
            Assert.Empty(Discover(P(D("“We must go,”"), N("said he."))));
        }

        [Fact]
        public void Discover_counts_every_tag_and_keeps_the_first_example_in_first_seen_order()
        {
            var found = Discover(
                P(D("“A,”"), N("said Nogamu.")),
                P(N("Laurie said,"), D("“B.”")),
                P(D("“C,”"), N("said Nogamu quietly.")));

            Assert.Equal(
                [new DiscoveredName("Nogamu", 2, "said Nogamu."), new DiscoveredName("Laurie", 1, "Laurie said,")],
                found);
        }
    }
}
