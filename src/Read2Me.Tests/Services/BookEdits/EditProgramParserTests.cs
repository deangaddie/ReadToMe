using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class EditProgramParserTests
    {
        private const string ValidProgram = """
            {
              "reasoning": "rename chapters",
              "supported": true,
              "unsupported_reason": null,
              "target": "chapter_title",
              "node_filter": { "ordinal_from": 3, "ordinal_to": 7, "title_regex": null },
              "paragraph_filter": { "where": [] },
              "transform": { "kind": "set_template", "pattern": null, "replacement": null, "template": "Chapter {n}: {old}", "instruction": null }
            }
            """;

        [Fact]
        public void TryParse_ValidProgram_MapsAllFields()
        {
            Assert.True(EditProgramParser.TryParse(ValidProgram, out var program, out _));
            Assert.NotNull(program);
            Assert.True(program!.Supported);
            Assert.Equal(EditTargetSelector.ChapterTitle, program.Target);
            Assert.Equal(3, program.NodeFilter.OrdinalFrom);
            Assert.Equal(7, program.NodeFilter.OrdinalTo);
            Assert.Equal(TransformKind.SetTemplate, program.Transform.Kind);
            Assert.Equal("Chapter {n}: {old}", program.Transform.Template);
        }

        [Fact]
        public void TryParse_CodeFencesAndProse_StillParses()
        {
            var raw = "Here is the plan:\n```json\n" + ValidProgram + "\n```";
            Assert.True(EditProgramParser.TryParse(raw, out var program, out _));
            Assert.Equal(EditTargetSelector.ChapterTitle, program!.Target);
        }

        [Fact]
        public void TryParse_Unsupported_ReturnsProgramWithReason()
        {
            var raw = """
                { "reasoning": "structural", "supported": false, "unsupported_reason": "Splitting chapters is not supported.",
                  "target": "chapter_title",
                  "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
                  "paragraph_filter": { "where": [] },
                  "transform": { "kind": "llm", "pattern": null, "replacement": null, "template": null, "instruction": null } }
                """;
            Assert.True(EditProgramParser.TryParse(raw, out var program, out _));
            Assert.False(program!.Supported);
            Assert.Equal("Splitting chapters is not supported.", program.UnsupportedReason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("no json here")]
        [InlineData("{ not valid json ]")]
        public void TryParse_Garbage_Fails(string raw)
        {
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.NotNull(error);
        }

        [Fact]
        public void TryParse_InvalidRegex_FailsWithMessage()
        {
            var raw = ValidProgram.Replace("\"title_regex\": null", "\"title_regex\": \"[unclosed\"");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains("regular expression", error);
        }

        [Fact]
        public void TryParse_RegexReplaceWithoutPattern_Fails()
        {
            var raw = ValidProgram.Replace("\"kind\": \"set_template\"", "\"kind\": \"regex_replace\"");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains("pattern", error);
        }

        [Fact]
        public void TryParse_LlmWithoutInstruction_Fails()
        {
            var raw = ValidProgram.Replace("\"kind\": \"set_template\"", "\"kind\": \"llm\"");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains("instruction", error);
        }

        [Fact]
        public void TryParse_UnknownTarget_Fails()
        {
            var raw = ValidProgram.Replace("chapter_title", "book_title");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains("target", error);
        }

        [Fact]
        public void TryParse_ParagraphWhere_MapsPredicates()
        {
            var raw = ValidProgram
                .Replace("chapter_title", "paragraph_text")
                .Replace("\"where\": []",
                    """
                    "where": [
                      { "field": "paragraph_ordinal", "op": "eq", "value": 2, "value_to": null, "regex": null },
                      { "field": "text", "op": "regex", "value": null, "value_to": null, "regex": "^\\s" }
                    ]
                    """);
            Assert.True(EditProgramParser.TryParse(raw, out var program, out _));
            Assert.Equal(EditTargetSelector.ParagraphText, program!.Target);
            Assert.Equal(2, program.ParagraphFilter.Where.Count);
            Assert.Equal(new EditPredicate(PredicateField.ParagraphOrdinal, PredicateOp.Eq, 2), program.ParagraphFilter.Where[0]);
            Assert.Equal(new EditPredicate(PredicateField.Text, PredicateOp.Regex, Regex: "^\\s"), program.ParagraphFilter.Where[1]);
        }

        [Theory]
        [InlineData("""{ "field": "chapter_ordinal", "op": "eq", "value": 1, "value_to": null, "regex": null }""", "field")]
        [InlineData("""{ "field": "paragraph_ordinal", "op": "near", "value": 1, "value_to": null, "regex": null }""", "op")]
        [InlineData("""{ "field": "paragraph_ordinal", "op": "eq", "value": null, "value_to": null, "regex": null }""", "value")]
        [InlineData("""{ "field": "paragraph_ordinal", "op": "between", "value": 1, "value_to": null, "regex": null }""", "value_to")]
        [InlineData("""{ "field": "paragraph_ordinal", "op": "regex", "value": null, "value_to": null, "regex": "x" }""", "regex")]
        [InlineData("""{ "field": "text", "op": "eq", "value": 1, "value_to": null, "regex": null }""", "text")]
        [InlineData("""{ "field": "text", "op": "regex", "value": null, "value_to": null, "regex": "[unclosed" }""", "regular expression")]
        public void TryParse_InvalidPredicate_FailsWithMessage(string predicate, string expectedInError)
        {
            var raw = ValidProgram
                .Replace("chapter_title", "paragraph_text")
                .Replace("\"where\": []", $"\"where\": [{predicate}]");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains(expectedInError, error);
        }

        [Fact]
        public void TryParse_MissingParagraphFilter_DefaultsToAll()
        {
            var raw = ValidProgram.Replace("\"paragraph_filter\": { \"where\": [] },", "");
            Assert.True(EditProgramParser.TryParse(raw, out var program, out _));
            Assert.Empty(program!.ParagraphFilter.Where);
        }
    }

    public class BookEditBatchParserTests
    {
        [Fact]
        public void TryParse_ValidArray_MapsByIndex()
        {
            var raw = """
                [ { "index": 0, "reasoning": "a", "new_text": "First" },
                  { "index": 2, "reasoning": "b", "new_text": "Third" } ]
                """;
            Assert.True(BookEditBatchParser.TryParse(raw, out var results));
            Assert.Equal(2, results.Count);
            Assert.Equal("First", results[0]);
            Assert.Equal("Third", results[2]);
        }

        [Fact]
        public void TryParse_FencesAndDuplicates_FirstWins()
        {
            var raw = "```json\n[ { \"index\": 0, \"reasoning\": \"a\", \"new_text\": \"Keep\" }, { \"index\": 0, \"reasoning\": \"b\", \"new_text\": \"Drop\" } ]\n```";
            Assert.True(BookEditBatchParser.TryParse(raw, out var results));
            Assert.Equal("Keep", results[0]);
        }

        [Fact]
        public void TryParse_EntriesWithoutIndexOrText_Dropped()
        {
            var raw = """
                [ { "reasoning": "no index", "new_text": "x" },
                  { "index": 1, "reasoning": "ok", "new_text": "Good" },
                  { "index": 5, "reasoning": "no text" } ]
                """;
            Assert.True(BookEditBatchParser.TryParse(raw, out var results));
            Assert.Single(results);
            Assert.Equal("Good", results[1]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("prose only")]
        [InlineData("[ broken")]
        public void TryParse_Garbage_Fails(string raw)
        {
            Assert.False(BookEditBatchParser.TryParse(raw, out _));
        }

        [Fact]
        public void TryParse_TruncatedMidString_SalvagesCompleteEntries()
        {
            // Shape seen when generation hits the token/context limit: complete
            // entries, then a final entry cut off inside its new_text string.
            var raw = """
                [
                  { "index": 0, "reasoning": "a", "new_text": "First" },
                  { "index": 1, "reasoning": "b", "new_text": "Second" },
                  { "index": 2, "reasoning": "c", "new_text": "Third is cut o
                """;
            Assert.True(BookEditBatchParser.TryParse(raw, out var results));
            Assert.Equal(2, results.Count);
            Assert.Equal("First", results[0]);
            Assert.Equal("Second", results[1]);
        }

        [Fact]
        public void TryParse_TruncatedAfterEntry_SalvagesCompleteEntries()
        {
            // Cut between entries: trailing comma, no closing bracket.
            var raw = """
                [
                  { "index": 6, "reasoning": "a", "new_text": "Sixth" },
                """;
            Assert.True(BookEditBatchParser.TryParse(raw, out var results));
            Assert.Single(results);
            Assert.Equal("Sixth", results[6]);
        }

        [Fact]
        public void TryParse_BracesAndBracketsInsideStrings_DoNotConfuseSalvage()
        {
            var raw = """
                [
                  { "index": 0, "reasoning": "has } and ] and \" inside", "new_text": "Text with ] bracket" },
                  { "index": 1, "reasoning": "b", "new_text": "cut of
                """;
            Assert.True(BookEditBatchParser.TryParse(raw, out var results));
            Assert.Single(results);
            Assert.Equal("Text with ] bracket", results[0]);
        }
    }
}
