using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters.ChapterPass;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter-pass prompt and its cache contract (spec §4.3): a system text fixed for the
    /// chapter, and a user message whose lines before the current paragraph only ever grow by an
    /// inserted <c>{Name} </c> label, so llama's prompt cache reuses the prefix.
    /// </summary>
    public class ChapterPassPromptTests
    {
        private static ContextItem Narr(string text) =>
            new(Guid.NewGuid(), text, AttributionWire.Narration, AttributionWire.Narrator);

        private static ContextItem Dialog(string text, string speaker = AttributionWire.Unknown) =>
            new(Guid.NewGuid(), text, AttributionWire.Dialog, speaker);

        private static ChapterParagraph Para(params ContextItem[] items) => new(Guid.NewGuid(), items);

        private static Character Char(string name, params string[] aliases) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Aliases = [.. aliases.Select(a => new CharacterAlias { Id = Guid.NewGuid(), Name = a })],
        };

        private static readonly Character SeedNarrator = new()
        {
            Id = ProjectDbContext.NarratorId, Name = ProjectDbContext.NarratorName, IsNarrator = true,
        };

        private static ChapterPassPrompt Build(
            IReadOnlyList<ChapterParagraph> snapshot, IEnumerable<ChapterParagraph> queued,
            IReadOnlyList<Character>? roster = null) =>
            new("Magician", "Raymond E. Feist", roster ?? [SeedNarrator, Char("Pug"), Char("Kulgan")],
                snapshot, queued.Select(p => p.ParagraphId).ToHashSet());

        private const string Question0 =
            "Who speaks ⟦0.1⟧, and how is it delivered? Answer as Name | delivery.";

        [Fact]
        public void System_text_is_the_lab_v1_prompt_in_name_and_voice_form()
        {
            var p0 = Para(Dialog("“Hi.”"));
            var prompt = Build([p0], [p0], [SeedNarrator, Char("Pug", "Squire Pug", "Pug of Crydee"), Char("Kulgan")]);

            Assert.Equal(
                """
                You identify who speaks each line of dialogue in the novel "Magician" by Raymond E. Feist.

                Each dialogue item in the passage is marked ⟦paragraph.item⟧. Items already attributed show the speaker as {Name} before the marker.

                Choices (the whole book's cast, NOT a list of who is present):
                - Pug (also: Squire Pug, Pug of Crydee)
                - Kulgan
                Unknown: not identified — the visible text has not yet named or uniquely described the speaker

                Rules:
                - Use attribution tags ("said X", "X replied") before or after the quote, actions by a named character in the same paragraph, who is addressed by name (usually NOT the speaker), and the alternation of a two-person conversation.
                - A listed character is a candidate only once the visible text has placed them in this scene. A speaker the text has only described ("a man", "the young officer") and not yet identified is "?", even if the book names them later.
                - Never pick a name by elimination or plausibility. A wrong name is worse than "?".
                - Answer as "Name | delivery": the name exactly as listed (or "Unknown"), then a few words for the voice actor on how the line sounds: emotion, tone, volume, pace (e.g. "angry, shouting", "soft, hesitant", "amused"), taken from the text and the situation. Not speech verbs ("said", "replied", "asked") and not gestures or actions. Leave the delivery empty if the line is plain, neutral speech.
                """.ReplaceLineEndings("\n"),
                prompt.SystemText);
        }

        [Fact]
        public void Seed_narrator_is_excluded_and_a_linked_narrator_character_is_listed()
        {
            var p0 = Para(Dialog("“Hi.”"));
            var prompt = Build([p0], [p0], [SeedNarrator, Char("Jonathan Harker"), Char("Dracula")]);

            Assert.DoesNotContain("- Narrator", prompt.SystemText);
            Assert.Contains("- Jonathan Harker\n", prompt.SystemText);
            Assert.Equal(["Jonathan Harker", "Dracula"], prompt.Names);
        }

        [Fact]
        public void A_roster_name_unknown_is_left_out_of_the_choices()
        {
            var p0 = Para(Dialog("“Hi.”"));
            var prompt = Build([p0], [p0], [SeedNarrator, Char("Pug"), Char("Unknown")]);

            Assert.Equal(["Pug"], prompt.Names);
            Assert.DoesNotContain("- Unknown", prompt.SystemText);
        }

        [Fact]
        public void User_message_renders_narration_plain_and_dialog_with_markers_and_labels()
        {
            var p0 = Para(Narr("The boy ran."), Dialog("“Wait!”"), Narr("he cried."));
            var p1 = Para(Dialog("“Come back,” said Kulgan.", "Kulgan"));
            var p2 = Para(Dialog("“No.”"));
            var prompt = Build([p0, p1, p2], [p0, p2]);

            Assert.Equal(
                "Passage:\n" +
                "[0] The boy ran. ⟦0.1⟧“Wait!” he cried.\n" +
                "[1] {Kulgan} ⟦1.0⟧“Come back,” said Kulgan.\n" +
                "[2] ⟦2.0⟧“No.”\n\n" +
                Question0,
                prompt.UserMessage(0, 1));
        }

        [Fact]
        public void Stamps_on_queued_paragraphs_are_hidden()
        {
            var p0 = Para(Dialog("“Hi.”", "Pug"));
            var p1 = Para(Dialog("“Ho.”", "Kulgan"));
            var prompt = Build([p0, p1], [p1]);

            var message = prompt.UserMessage(1, 0);

            Assert.Contains("[0] {Pug} ⟦0.0⟧“Hi.”\n", message);
            Assert.Contains("[1] ⟦1.0⟧“Ho.”", message);
            Assert.DoesNotContain("{Kulgan}", message);
        }

        [Fact]
        public void Passage_runs_from_the_chapter_start_to_four_paragraphs_past_the_current()
        {
            var paras = Enumerable.Range(0, 10).Select(i => Para(Dialog($"“L{i}”"))).ToList();
            var prompt = Build(paras, paras);

            var message = prompt.UserMessage(3, 0);

            Assert.StartsWith("Passage:\n[0] ⟦0.0⟧“L0”\n", message);
            Assert.Contains("[7] ⟦7.0⟧“L7”\n\n", message);
            Assert.DoesNotContain("[8]", message);
        }

        [Fact]
        public void Set_label_shows_the_name_before_the_marker_and_k_is_stable()
        {
            var p0 = Para(Dialog("“A”"), Narr("x"), Dialog("“B”"));
            var p1 = Para(Dialog("“C”"));
            var prompt = Build([p0, p1], [p0, p1]);

            prompt.SetLabel(0, 2, "Pug");

            Assert.Contains("[0] ⟦0.0⟧“A” x {Pug} ⟦0.2⟧“B”\n[1] ⟦1.0⟧“C”", prompt.UserMessage(1, 0));
        }

        /// <summary>
        /// The prefix property, over a sequential walk of every dialog item: each call's passage up to
        /// and including the current paragraph's line, with the one label set since inserted, is the
        /// start of the next call's message.
        /// </summary>
        [Fact]
        public void Consecutive_asks_are_append_only_up_to_the_current_paragraph()
        {
            var paras = new List<ChapterParagraph>
            {
                Para(Narr("Night fell."), Dialog("“Who's there?”"), Narr("asked Pug.")),
                Para(Dialog("“Me.”"), Dialog("“Who?”")),
                Para(Narr("Silence.")),
                Para(Dialog("“Kulgan!”", "Kulgan")),
                Para(Dialog("“Go.”")),
            };
            var queued = new[] { paras[0], paras[1], paras[4] };
            var prompt = Build(paras, queued);

            var asks = new List<(int K, int Ii)>();
            for (var k = 0; k < paras.Count; k++)
                if (queued.Contains(paras[k]))
                    for (var ii = 0; ii < paras[k].Items.Count; ii++)
                        if (paras[k].Items[ii].IsDialog)
                            asks.Add((k, ii));

            var system = prompt.SystemText;
            for (var n = 0; n < asks.Count - 1; n++)
            {
                var (k, ii) = asks[n];
                var before = prompt.UserMessage(k, ii);
                var name = n % 2 == 0 ? "Pug" : "Kulgan";
                prompt.SetLabel(k, ii, name);
                var after = prompt.UserMessage(asks[n + 1].K, asks[n + 1].Ii);

                var lineStart = before.IndexOf($"\n[{k}] ", StringComparison.Ordinal);
                var upToCurrent = before[..(before.IndexOf('\n', lineStart + 1) + 1)];
                var marker = $"⟦{k}.{ii}⟧";
                var expected = upToCurrent.Replace(marker, "{" + name + "} " + marker, StringComparison.Ordinal);

                Assert.StartsWith(expected, after, StringComparison.Ordinal);
                Assert.Equal(system, prompt.SystemText);
            }
        }

        [Fact]
        public void A_trim_drops_the_oldest_half_of_the_lines_before_the_current_and_keeps_k()
        {
            var paras = Enumerable.Range(0, 12).Select(i => Para(Dialog($"“L{i}”"))).ToList();
            var prompt = Build(paras, paras);

            Assert.Equal(0, prompt.TrimStart);
            Assert.True(prompt.Trim(6));

            Assert.Equal(3, prompt.TrimStart);
            Assert.Equal(
                "Passage:\n[3] ⟦3.0⟧“L3”\n[4] ⟦4.0⟧“L4”\n[5] ⟦5.0⟧“L5”\n[6] ⟦6.0⟧“L6”\n" +
                "[7] ⟦7.0⟧“L7”\n[8] ⟦8.0⟧“L8”\n[9] ⟦9.0⟧“L9”\n[10] ⟦10.0⟧“L10”\n\n" +
                "Who speaks ⟦6.0⟧, and how is it delivered? Answer as Name | delivery.",
                prompt.UserMessage(6, 0));
        }

        [Fact]
        public void Nothing_is_trimmed_at_or_past_the_current_paragraph()
        {
            var paras = Enumerable.Range(0, 3).Select(i => Para(Dialog($"“L{i}”"))).ToList();
            var prompt = Build(paras, paras);

            Assert.True(prompt.Trim(1));
            Assert.Equal(1, prompt.TrimStart);
            Assert.False(prompt.Trim(1));
            Assert.Equal(1, prompt.TrimStart);
            Assert.StartsWith("Passage:\n[1] ⟦1.0⟧“L1”\n", prompt.UserMessage(1, 0));
        }

        [Fact]
        public void Needs_trim_measures_the_system_text_the_passage_and_the_question()
        {
            var paras = Enumerable.Range(0, 80).Select(_ => Para(Dialog($"“{new string('x', 600)}”"))).ToList();
            var prompt = Build(paras, paras);

            Assert.False(prompt.NeedsTrim(10, 0));
            Assert.True(prompt.NeedsTrim(70, 0));
            Assert.Equal(
                prompt.SystemText.Length + prompt.UserMessage(60, 0).Length > ChapterPassBudget.MaxPromptChars,
                prompt.NeedsTrim(60, 0));
        }

        /// <summary>The prefix property holds again from <c>trimStart</c> once a trim has happened.</summary>
        [Fact]
        public void After_a_trim_consecutive_asks_are_append_only_again()
        {
            var paras = Enumerable.Range(0, 20).Select(i => Para(Narr($"N{i}."), Dialog($"“L{i}”"))).ToList();
            var prompt = Build(paras, paras);

            Assert.True(prompt.Trim(10));
            for (var k = 10; k < 15; k++)
            {
                var before = prompt.UserMessage(k, 1);
                prompt.SetLabel(k, 1, "Pug");
                var after = prompt.UserMessage(k + 1, 1);

                var lineStart = before.IndexOf($"\n[{k}] ", StringComparison.Ordinal);
                var upToCurrent = before[..(before.IndexOf('\n', lineStart + 1) + 1)];
                var expected = upToCurrent.Replace($"⟦{k}.1⟧", "{Pug} " + $"⟦{k}.1⟧", StringComparison.Ordinal);

                Assert.StartsWith("Passage:\n[5] N5. ⟦5.1⟧“L5”\n", after, StringComparison.Ordinal);
                Assert.StartsWith(expected, after, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Display_tail_is_the_last_five_passage_lines_and_the_question()
        {
            var paras = Enumerable.Range(0, 10).Select(i => Para(Dialog($"“L{i}”"))).ToList();
            var prompt = Build(paras, paras);

            Assert.Equal(
                "Passage (tail):\n[3] ⟦3.0⟧“L3”\n[4] ⟦4.0⟧“L4”\n[5] ⟦5.0⟧“L5”\n[6] ⟦6.0⟧“L6”\n[7] ⟦7.0⟧“L7”\n\n" +
                "Who speaks ⟦3.0⟧, and how is it delivered? Answer as Name | delivery.",
                prompt.DisplayTail(3, 0));
        }
    }
}
