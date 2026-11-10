using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Read2Me.AppData.Entities;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    /// <summary>
    /// Live planner eval, run on demand: replays the planner instruction set (change_case, regex, llm
    /// and Edit-source cases) against a real llama server with the planner's own prompt and schema,
    /// thinking on and off. Skipped unless READ2ME_LIVE_LLM_URL names the server (e.g.
    /// http://localhost:8080); READ2ME_LIVE_LLM_MODEL overrides the preset (default qwen-28b). Pass =
    /// kind, case mode, target and supported exact; the pattern equal to the expected one or matching
    /// the same spans over the case's sample text; sources equal as a set; a template rendering to the
    /// expected value against the case's fixture; an llm instruction naming every source. An
    /// unsupported case passes when either the model or the parser refuses (the output says which).
    /// </summary>
    public class EditPlanLiveEvalTests
    {
        private const string UrlVariable = "READ2ME_LIVE_LLM_URL";
        private const string ModelVariable = "READ2ME_LIVE_LLM_MODEL";
        private const string DefaultModel = "qwen-28b";
        private const int TaleChapters = 45;

        // All-caps spans in context from Project Gutenberg books (research 01): headings, a quoted
        // sign, numerals, a front-matter line and PG.
        private const string AllCapsSample = """
            “MY DEAR MR. SHERLOCK HOLMES,—You really did it very well.
            JONATHAN HARKER’S JOURNAL
            it was labelled “ORANGE MARMALADE”, but to her great disappointment it was empty
            THE FIVE ORANGE PIPS
            daughter of Louis XI, and married in 1499 Anne of Brittany, widow of Charles VIII
            Most people start at our website which has the main PG search facility: www.gutenberg.org.
            PRINTED IN THE UNITED STATES AT THE COUNTRY LIFE PRESS, GARDEN CITY, N.Y.
            CHAPTER IV
            """;

        /// <summary>The pattern a case expects and the text its spans are compared over.</summary>
        public sealed record ExpectedPattern(string Pattern, string Sample);

        /// <summary>Which hand-written Tale outline a case sends: <see cref="Default"/> has titled chapters with
        /// real opening lines; the others have untitled chapters whose first paragraphs are "1" / "The Period"
        /// (<see cref="Bare"/>) or "CHAPTER I." / "The Period." (<see cref="Roman"/>).</summary>
        public enum Shape { Default, Roman, Bare }

        /// <summary>
        /// What a source-reading plan must do: name exactly <see cref="Names"/> (as a set); a
        /// set_template renders against <see cref="Fixture"/> (with <see cref="Old"/> as {old}) to a
        /// string that passes <see cref="RenderOk"/>; an llm instruction names every source.
        /// </summary>
        public sealed record ExpectedSources(
            string[] Names, Dictionary<string, string> Fixture, string Old, Func<string, bool> RenderOk);

        public sealed record EvalCase(
            string Instruction, TransformKind? Kind, EditTargetSelector? Target, CaseMode? Mode,
            ExpectedPattern? Pattern = null, Shape Outline = Shape.Default, ExpectedSources? Sources = null,
            bool Unsupported = false, bool ChecksFirstParagraphScope = false)
        {
            public override string ToString() => Outline == Shape.Bare ? $"{Instruction} [bare]" : Instruction;
        }

        private static readonly ExpectedPattern AllCaps = new(EditProgramSchema.AllCapsPattern, AllCapsSample);

        private const string Motivating =
            "Each chapter has its chapter number in its first paragraph, and its title in the second paragraph. " +
            "Update the chapter's title to be `Chapter <number from paragraph 1>, <title from paragraph 2>`";

        private static readonly EvalCase[] Cases =
        [
            new("make the all-caps words in A Tale of Two Cities sentence case", TransformKind.ChangeCase,
                EditTargetSelector.ParagraphText, CaseMode.Sentence, AllCaps),
            new("the headings are in ALL CAPS, change them to normal case", TransformKind.ChangeCase,
                EditTargetSelector.ParagraphText, CaseMode.Sentence, AllCaps),
            new("fix the shouty capitals", TransformKind.ChangeCase,
                EditTargetSelector.ParagraphText, CaseMode.Sentence, AllCaps),
            new("title case the chapter titles", TransformKind.ChangeCase,
                EditTargetSelector.ChapterTitle, CaseMode.Title),
            new("make the part titles uppercase", TransformKind.ChangeCase,
                EditTargetSelector.PartTitle, CaseMode.Upper),
            new("fix the grammar in chapter 3", TransformKind.Llm,
                EditTargetSelector.ParagraphText, null),
            new("replace HELlo with heLO", TransformKind.RegexReplace,
                EditTargetSelector.ParagraphText, null, new("HELlo", "HELlo, hello, Hello and HELLO.")),
            new("replace Mr. with Mister", TransformKind.RegexReplace,
                EditTargetSelector.ParagraphText, null, new(@"Mr\.", "Mr. Darcy met Mrs Bennet, Mr, and Mr. Collins; Mrx.")),

            // 1: a bare number then the title is already final, so the tokens copy it.
            new(Motivating, TransformKind.SetTemplate, EditTargetSelector.ChapterTitle, null,
                Outline: Shape.Bare, Sources: new(["p1", "p2"],
                    new() { ["p1"] = "1", ["p2"] = "The Period" }, "", s => s == "Chapter 1, The Period")),
            // 2: a roman numeral has to become a digit, so the model reads it.
            new(Motivating, TransformKind.Llm, EditTargetSelector.ChapterTitle, null,
                Outline: Shape.Roman, Sources: new(["p1", "p2"], [], "", _ => true)),
            // 3: a paragraph target reads the chapter title; scope is the first paragraph only.
            new("prefix each chapter's first paragraph with the chapter title", TransformKind.SetTemplate,
                EditTargetSelector.ParagraphText, null, ChecksFirstParagraphScope: true,
                Sources: new(["chapter"], new() { ["chapter"] = "The Period" }, "It was the best of times",
                    s => s.StartsWith("The Period", StringComparison.Ordinal) && s.Contains("It was the best of times"))),
            // 4 and 5: a volume can't read paragraphs; a seventh paragraph is past the depth cap.
            new("name the volume after the book's first paragraph", null, null, null, Unsupported: true),
            new("set each chapter title to its seventh paragraph", null, null, null, Unsupported: true),
            // 6: the wording asks for an exact copy, which beats what the sample would suggest.
            new("set each chapter title to exactly paragraph 1 and paragraph 2, joined by a colon and a space",
                TransformKind.SetTemplate, EditTargetSelector.ChapterTitle, null,
                Outline: Shape.Roman, Sources: new(["p1", "p2"], new() { ["p1"] = "CHAPTER I.", ["p2"] = "The Period." }, "",
                    s => s == "CHAPTER I.: The Period.")),
            // 7: the last paragraph isn't in the sample, so the model can't have checked it.
            new("set each chapter title to the chapter number written in its last paragraph",
                TransformKind.Llm, EditTargetSelector.ChapterTitle, null,
                Sources: new(["p_end1"], [], "", _ => true)),
        ];

        public static TheoryData<EvalCase, bool> CasesWithThinking()
        {
            var data = new TheoryData<EvalCase, bool>();
            foreach (var c in Cases)
            {
                data.Add(c, true);
                data.Add(c, false);
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(CasesWithThinking))]
        public async Task Plan_PicksTheExpectedProgram(EvalCase evalCase, bool thinking)
        {
            var baseUrl = Environment.GetEnvironmentVariable(UrlVariable);
            if (string.IsNullOrEmpty(baseUrl))
                Assert.Skip($"{UrlVariable} is not set (a running llama server with {DefaultModel})");

            var raw = await PlanAsync(baseUrl, evalCase.Instruction, evalCase.Outline, thinking);
            var output = TestContext.Current.TestOutputHelper;
            output?.WriteLine(raw);

            Assert.True(EditProgramParser.TryParse(raw, out var program, out var error), error);

            if (evalCase.Unsupported)
            {
                // Either refusal passes; the log says which one it was.
                output?.WriteLine(ModelSaidUnsupported(raw)
                    ? "UNSUPPORTED: refused by the model"
                    : "UNSUPPORTED: refused by the parser backstop: " + program!.UnsupportedReason);
                Assert.False(program!.Supported, "the plan should be unsupported");
                return;
            }

            Assert.True(program!.Supported, program.UnsupportedReason);
            Assert.Equal(evalCase.Kind, program.Transform.Kind);
            Assert.Equal(evalCase.Target, program.Target);
            Assert.Equal(evalCase.Mode, program.Transform.CaseMode);
            if (evalCase.Kind is TransformKind.ChangeCase or TransformKind.RegexReplace)
                AssertSamePattern(evalCase.Pattern, program.Transform.Pattern);
            if (evalCase.Sources is { } expected)
                AssertSources(expected, program.Transform);
            if (evalCase.ChecksFirstParagraphScope)
                Assert.Contains(program.ParagraphFilter.Where, p => p is
                    { Field: PredicateField.ParagraphOrdinal, Op: PredicateOp.Eq or PredicateOp.Le, Value: 1 });
        }

        private static bool ModelSaidUnsupported(string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("supported", out var s) && s.ValueKind == JsonValueKind.False;
        }

        private static void AssertSources(ExpectedSources expected, EditTransform transform)
        {
            Assert.True(EditPlanEvalScoring.SameSources(expected.Names, transform.Sources),
                $"sources were [{string.Join(", ", transform.Sources)}], expected [{string.Join(", ", expected.Names)}]");
            if (transform.Kind == TransformKind.SetTemplate)
            {
                var rendered = EditPlanEvalScoring.Render(transform.Template ?? "", expected.Fixture, 1, expected.Old);
                Assert.True(expected.RenderOk(rendered), $"template '{transform.Template}' rendered '{rendered}'");
            }
            else
            {
                Assert.True(EditPlanEvalScoring.MentionsEach(transform.Instruction, expected.Names),
                    $"instruction '{transform.Instruction}' does not name every source");
            }
        }

        private static void AssertSamePattern(ExpectedPattern? expected, string? actual)
        {
            if (expected == null || actual == expected.Pattern)
            {
                Assert.Equal(expected?.Pattern, actual);
                return;
            }
            Assert.NotNull(actual);
            Assert.Equal(Spans(expected.Pattern, expected.Sample), Spans(actual, expected.Sample));
        }

        private static IEnumerable<string> Spans(string pattern, string text) =>
            Regex.Matches(text, pattern).Select(m => $"{m.Index}:{m.Value}").ToList();

        /// <summary>The planner's request as BookEditPlanner sends it, posted unstreamed.</summary>
        private static async Task<string> PlanAsync(string baseUrl, string instruction, Shape shape, bool thinking)
        {
            var config = new LlmServerConfig
            {
                Name = "live-eval", BaseUrl = baseUrl,
                Model = Environment.GetEnvironmentVariable(ModelVariable) ?? DefaultModel,
            };
            var prompt = BookEditPlanner.RenderPrompt("A Tale of Two Cities", "Charles Dickens", instruction, TaleOutline(shape));
            var body = OpenAiRequestBuilder.BuildChatBody(config, prompt, stream: false,
                EditProgramSchema.JsonSchema, disableThinking: !thinking);

            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.PostAsync("/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            return reply.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        // Real headings of A Tale of Two Cities (Project Gutenberg): the first book's six chapters, then
        // the second book's first. The roman numerals restart in each book.
        private static readonly (string Book, string Roman, string Title)[] TaleChapterList =
        [
            ("Book the First: Recalled to Life", "I", "The Period"), ("", "II", "The Mail"),
            ("", "III", "The Night Shadows"), ("", "IV", "The Preparation"), ("", "V", "The Wine-shop"),
            ("", "VI", "The Shoemaker"),
            ("Book the Second: The Golden Thread", "I", "Five Years Later"),
        ];

        // Each chapter's opening paragraphs, after its heading paragraphs.
        private static readonly string[][] TaleOpenings =
        [
            [
                "It was the best of times, it was the worst of times, it was the age of wisdom, it was the age of foolishness, it was the epoch of belief, it was the epoch of incredulity",
                "There were a king with a large jaw and a queen with a plain face, on the throne of England; there were a king with a large jaw and a queen with a fair face, on the throne of France.",
                "It was the year of Our Lord one thousand seven hundred and seventy-five.",
            ],
            [
                "It was the Dover road that lay, on a Friday night late in November, before the first of the persons with whom this history has business.",
                "The Dover road lay, as to him, beyond the Dover mail, as it lumbered up Shooter’s Hill.",
                "He walked up hill in the mire by the side of the mail, as the rest of the passengers did.",
            ],
            [
                "A wonderful fact to reflect upon, that every human creature is constituted to be that profound secret and mystery to every other.",
                "A solemn consideration, when I enter a great city by night, that every one of those darkly clustered houses encloses its own secret.",
            ],
            [
                "When the mail got successfully to Dover, in the course of the forenoon, the head drawer at the Royal George Hotel opened the coach-door as his custom was.",
            ],
            [
                "A large cask of wine had been dropped and broken, in the street.",
                "The accident had happened in getting it out of a cart; the cask had tumbled out with a run, the hoops had burst, and it lay on the stones just outside the door of the wine-shop.",
            ],
            [
                "“Good day!” said Monsieur Defarge, looking down at the white head that bent low over the shoemaker’s bench.",
            ],
            [
                "Tellson’s Bank by Temple Bar was an old-fashioned place, even in the year one thousand seven hundred and eighty.",
            ],
        ];

        /// <summary>
        /// Hand-written in the shape <see cref="ChapterOutlineBuilder.BuildForEditAsync"/> gives A Tale of
        /// Two Cities (1 volume, 3 books as parts, 45 chapters, here cut to the first seven): Volume and
        /// Part lines, a chapter heading, and sample lines named p1… under it. The default has titled
        /// chapters and the opening paragraphs; the roman shape has untitled chapters whose first two
        /// paragraphs are the Gutenberg headings ("CHAPTER I." then "The Period."), the bare shape "1"
        /// then "The Period", each followed by the opening paragraphs.
        /// </summary>
        private static string TaleOutline(Shape shape)
        {
            var sb = new StringBuilder($"1 volume(s), 3 part(s), {TaleChapters} chapter(s).\n");
            sb.AppendLine("Volume 1: A Tale of Two Cities");
            var part = 0;
            var numberInBook = 0;
            for (var i = 0; i < TaleChapterList.Length; i++)
            {
                var (book, roman, title) = TaleChapterList[i];
                if (book != "")
                {
                    sb.AppendLine($"  Part {++part}: {book}");
                    numberInBook = 0;
                }
                numberInBook++;
                var line = 1;
                if (shape == Shape.Default)
                {
                    sb.AppendLine($"    Chapter {i + 1}: {title}");
                }
                else
                {
                    sb.AppendLine($"    Chapter {i + 1}: (untitled)");
                    sb.AppendLine($"      p{line++}: {(shape == Shape.Bare ? numberInBook.ToString() : $"CHAPTER {roman}.")}");
                    sb.AppendLine($"      p{line++}: {(shape == Shape.Bare ? title : title + ".")}");
                }
                foreach (var paragraph in TaleOpenings[i])
                    sb.AppendLine($"      p{line++}: {Cut(paragraph)}");
            }
            sb.AppendLine($"... and {TaleChapters - TaleChapterList.Length} more chapters.");
            return sb.ToString();
        }

        private static string Cut(string value) => value.Length <= 80 ? value : value[..80] + "…";
    }
}
