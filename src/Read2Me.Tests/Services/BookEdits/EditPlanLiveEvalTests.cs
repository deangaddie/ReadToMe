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
    /// Live planner eval, run on demand: replays the change_case instruction set against a real llama
    /// server with the planner's own prompt and schema, thinking on and off. Skipped unless
    /// READ2ME_LIVE_LLM_URL names the server (e.g. http://localhost:8080); READ2ME_LIVE_LLM_MODEL
    /// overrides the preset (default qwen-28b). Pass = kind, case mode and target exact, and the
    /// pattern equal to the expected one or matching the same spans over the case's sample text.
    /// </summary>
    public class EditPlanLiveEvalTests
    {
        private const string UrlVariable = "READ2ME_LIVE_LLM_URL";
        private const string ModelVariable = "READ2ME_LIVE_LLM_MODEL";
        private const string DefaultModel = "qwen-28b";
        private const int FoundationChapters = 53;

        // Foundation's all-caps spans in context (research 01): headings, headwords, numerals, UV.
        private const string AllCapsSample = """
            HARI SELDON— . . . born in the 11,988th year of the Galactic Era;
            ENCYCLOPEDIA GALACTICA
            “TAXIS TO ALL POINTS.”
            COMMISSION OF PUBLIC SAFETY— . . . the last strong Emperor, Cleon II. The first Chief Commissioner.
            THE MAYORS
            THE FOUR KINGDOMS— The name given to those portions of the Province of Anacreon
            I’m just a sucker who happened to land on Glyptal IV the day after the mail.
            it works on Korell, where UV light is not to be found on street corners.
            ISAAC ASIMOV began his Foundation Series at the age of twenty-one,
            THE FOUNDATION NOVELS
            PART IV
            """;

        /// <summary>The pattern a case expects and the text its spans are compared over.</summary>
        public sealed record ExpectedPattern(string Pattern, string Sample);

        public sealed record EvalCase(
            string Instruction, TransformKind Kind, EditTargetSelector Target, CaseMode? Mode,
            ExpectedPattern? Pattern = null)
        {
            public override string ToString() => Instruction;
        }

        private static readonly ExpectedPattern AllCaps = new(EditProgramSchema.AllCapsPattern, AllCapsSample);

        private static readonly EvalCase[] Cases =
        [
            new("make the all-caps words in Foundation sentence case", TransformKind.ChangeCase,
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
                EditTargetSelector.ParagraphText, null, new(@"Mr\.", "Mr. Mallow met Mrs Jael, Mr, and Mr. Sutt; Mrx.")),
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

            var raw = await PlanAsync(baseUrl, evalCase.Instruction, thinking);
            TestContext.Current.TestOutputHelper?.WriteLine(raw);

            Assert.True(EditProgramParser.TryParse(raw, out var program, out var error), error);
            Assert.True(program!.Supported, program.UnsupportedReason);
            Assert.Equal(evalCase.Kind, program.Transform.Kind);
            Assert.Equal(evalCase.Target, program.Target);
            Assert.Equal(evalCase.Mode, program.Transform.CaseMode);
            if (evalCase.Kind != TransformKind.Llm)
                AssertSamePattern(evalCase.Pattern, program.Transform.Pattern);
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
        private static async Task<string> PlanAsync(string baseUrl, string instruction, bool thinking)
        {
            var config = new LlmServerConfig
            {
                Name = "live-eval", BaseUrl = baseUrl,
                Model = Environment.GetEnvironmentVariable(ModelVariable) ?? DefaultModel,
            };
            var prompt = BookEditPlanner.RenderPrompt("Foundation", "Isaac Asimov", instruction, FoundationOutline());
            var body = OpenAiRequestBuilder.BuildChatBody(config, prompt, stream: false,
                EditProgramSchema.JsonSchema, disableThinking: !thinking);

            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.PostAsync("/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            return reply.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        // Shaped like ChapterOutlineBuilder's output for Foundation: 5 parts, numbered chapters.
        private static string FoundationOutline()
        {
            var sb = new StringBuilder($"1 volume(s), 5 part(s), {FoundationChapters} chapter(s).\n");
            for (var n = 1; n <= ChapterOutlineBuilder.ChapterLimit; n++)
                sb.AppendLine($"Chapter {n}: {(n == 1 ? "(untitled)" : (n - 1).ToString())}");
            sb.AppendLine($"... and {FoundationChapters - ChapterOutlineBuilder.ChapterLimit} more chapters.");
            return sb.ToString();
        }
    }
}
