using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    /// <summary>
    /// Live batch eval, run on demand: one batch of chapter-title entries carrying "sources" (the
    /// roman shape of A Tale of Two Cities: p1 "CHAPTER IV.", p2 "The Preparation.") goes through the
    /// production batch prompt and schema, with the instruction a planner would write for it, thinking
    /// on and off. Skipped unless READ2ME_LIVE_LLM_URL names the server (the variable
    /// <see cref="EditPlanLiveEvalTests"/> uses); READ2ME_LIVE_LLM_MODEL overrides the preset (default
    /// qwen-28b). Pass = every <c>new_text</c> exactly the expected title.
    /// </summary>
    public class BatchEditLiveEvalTests
    {
        private const string UrlVariable = "READ2ME_LIVE_LLM_URL";
        private const string ModelVariable = "READ2ME_LIVE_LLM_MODEL";
        private const string DefaultModel = "qwen-28b";

        private const string Instruction =
            "Set the chapter title to \"Chapter <N>, <title>\", where <N> is the chapter number written in " +
            "p1 (a roman numeral, for example CHAPTER IV.) as an ordinary digit number, and <title> is p2 " +
            "copied exactly as written, including its final full stop.";

        // Real chapter headings from A Tale of Two Cities (Project Gutenberg), roman shape.
        private static readonly (string P1, string P2, string Expected)[] Chapters =
        [
            ("CHAPTER I.", "The Period.", "Chapter 1, The Period."),
            ("CHAPTER IV.", "The Preparation.", "Chapter 4, The Preparation."),
            ("CHAPTER IX.", "The Gorgon’s Head.", "Chapter 9, The Gorgon’s Head."),
            ("CHAPTER XIV.", "The Honest Tradesman.", "Chapter 14, The Honest Tradesman."),
            ("CHAPTER XLV.", "The Footsteps Die Out For Ever.", "Chapter 45, The Footsteps Die Out For Ever."),
        ];

        public static TheoryData<bool> Thinking() => new() { true, false };

        [Theory]
        [MemberData(nameof(Thinking))]
        public async Task Batch_WritesEachTitleFromItsSources(bool thinking)
        {
            var baseUrl = Environment.GetEnvironmentVariable(UrlVariable);
            if (string.IsNullOrEmpty(baseUrl))
                Assert.Skip($"{UrlVariable} is not set (a running llama server with {DefaultModel})");

            var batch = Chapters.Select((c, i) => new EditTarget(
                BookEditTargetKind.ChapterTitle, Guid.NewGuid(), "", $"Chapter {i + 1}", i + 1, null, null)
            {
                Sources = [new("p1", c.P1, null), new("p2", c.P2, null)],
            }).ToList();

            var raw = await SendAsync(baseUrl, batch, thinking);
            TestContext.Current.TestOutputHelper?.WriteLine(raw);

            Assert.True(BookEditBatchParser.TryParse(raw, out var results), "the reply did not parse");
            Assert.Equal(Chapters.Select(c => c.Expected), Chapters.Select((_, i) => results.GetValueOrDefault(i)));
        }

        /// <summary>The request BookEditProposalService sends for a batch, posted unstreamed.</summary>
        private static async Task<string> SendAsync(string baseUrl, IReadOnlyList<EditTarget> batch, bool thinking)
        {
            var config = new LlmServerConfig
            {
                Name = "live-eval", BaseUrl = baseUrl,
                Model = Environment.GetEnvironmentVariable(ModelVariable) ?? DefaultModel,
            };
            var prompt = BookEditProposalService.RenderBatchPrompt(
                "A Tale of Two Cities", "Charles Dickens", Instruction, batch);
            var body = OpenAiRequestBuilder.BuildChatBody(config, prompt, stream: false,
                BookEditBatchSchema.JsonSchema, disableThinking: !thinking);

            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.PostAsync("/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            return reply.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }
}
