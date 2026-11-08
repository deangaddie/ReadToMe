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

        [Theory]
        [InlineData("upper", CaseMode.Upper)]
        [InlineData("lower", CaseMode.Lower)]
        [InlineData("sentence", CaseMode.Sentence)]
        [InlineData("title", CaseMode.Title)]
        public void TryParse_ChangeCase_MapsCaseMode(string wire, CaseMode expected)
        {
            var raw = ValidProgram
                .Replace("\"kind\": \"set_template\"", "\"kind\": \"change_case\"")
                .Replace("\"template\": \"Chapter {n}: {old}\"", "\"template\": null")
                .Replace("\"instruction\": null", $"\"instruction\": null, \"case_mode\": \"{wire}\"");
            Assert.True(EditProgramParser.TryParse(raw, out var program, out var error), error);
            Assert.Equal(TransformKind.ChangeCase, program!.Transform.Kind);
            Assert.Equal(expected, program.Transform.CaseMode);
            Assert.Null(program.Transform.Pattern);
        }

        [Theory]
        [InlineData(", \"case_mode\": null")]
        [InlineData("")]
        public void TryParse_ChangeCaseWithoutCaseMode_Fails(string caseMode)
        {
            var raw = ValidProgram
                .Replace("\"kind\": \"set_template\"", "\"kind\": \"change_case\"")
                .Replace("\"instruction\": null", $"\"instruction\": null{caseMode}");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains("case_mode", error);
        }

        [Fact]
        public void TryParse_UnknownCaseMode_Fails()
        {
            var raw = ValidProgram
                .Replace("\"kind\": \"set_template\"", "\"kind\": \"change_case\"")
                .Replace("\"instruction\": null", "\"instruction\": null, \"case_mode\": \"shouty\"");
            Assert.False(EditProgramParser.TryParse(raw, out _, out var error));
            Assert.Contains("shouty", error);
        }

        [Fact]
        public void TryParse_OtherKindIgnoresCaseMode()
        {
            var raw = ValidProgram.Replace("\"instruction\": null", "\"instruction\": null, \"case_mode\": null");
            Assert.True(EditProgramParser.TryParse(raw, out var program, out _));
            Assert.Null(program!.Transform.CaseMode);
        }

        [Theory]
        [InlineData("'", "'")]
        [InlineData("’", "’")]
        [InlineData("'", "’")]
        public void TryParse_ChangeCase_AllCapsPatternCopiedWithOtherApostrophes_IsTheExactPattern(
            string first, string second)
        {
            // qwen-28b may copy the escaped ’ as ' (which would skip HARKER’S) or as a literal ’.
            var copied = ReplaceEach(EditProgramSchema.AllCapsPattern, EditProgramSchema.EscapedTypographicApostrophe, first, second);
            Assert.NotEqual(EditProgramSchema.AllCapsPattern, copied);

            var program = Parsed(Plan("paragraph_text", "change_case", pattern: copied, caseMode: "sentence"));

            Assert.Equal(EditProgramSchema.AllCapsPattern, program.Transform.Pattern);
        }

        [Fact]
        public void TryParse_RegexReplace_AllCapsPatternCopiedWithStraightApostrophes_IsKeptAsWritten()
        {
            var copied = EditProgramSchema.AllCapsPattern.Replace(EditProgramSchema.EscapedTypographicApostrophe, "'");

            var program = Parsed(Plan("paragraph_text", "regex_replace", pattern: copied));

            Assert.Equal(copied, program.Transform.Pattern);
        }

        [Fact]
        public void TryParse_ChangeCase_OtherPattern_IsKeptAsWritten()
        {
            const string own = @"\b[A-Z]{2,}\b";

            var program = Parsed(Plan("paragraph_text", "change_case", pattern: own, caseMode: "sentence"));

            Assert.Equal(own, program.Transform.Pattern);
        }

        /// <summary>Replaces each occurrence of <paramref name="find"/> in turn with the next of
        /// <paramref name="replacements"/>, cycling through them.</summary>
        private static string ReplaceEach(string s, string find, params string[] replacements)
        {
            var parts = s.Split(find);
            var sb = new System.Text.StringBuilder(parts[0]);
            for (var i = 1; i < parts.Length; i++)
                sb.Append(replacements[(i - 1) % replacements.Length]).Append(parts[i]);
            return sb.ToString();
        }

        [Fact]
        public void JsonExample_ParsesAsTheSchemaShape()
        {
            Assert.True(EditProgramParser.TryParse(EditProgramSchema.JsonExample, out var program, out var error), error);
            Assert.Equal(TransformKind.SetTemplate, program!.Transform.Kind);
            Assert.Contains("\"case_mode\": null", EditProgramSchema.JsonExample);
            Assert.Contains("\"sources\": []", EditProgramSchema.JsonExample);
            Assert.Empty(program.Transform.Sources);
        }

        [Fact]
        public void TryParse_MissingParagraphFilter_DefaultsToAll()
        {
            var raw = ValidProgram.Replace("\"paragraph_filter\": { \"where\": [] },", "");
            Assert.True(EditProgramParser.TryParse(raw, out var program, out _));
            Assert.Empty(program!.ParagraphFilter.Where);
        }

        // ── Edit sources ──────────────────────────────────────────────────────

        private static string Plan(string target, string kind, string? template = null, string? instruction = null,
            string sources = "[]", string? pattern = null, string? caseMode = null)
        {
            static string Json(string? s) => s == null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);
            return $$"""
                { "reasoning": "r", "supported": true, "unsupported_reason": null, "target": "{{target}}",
                  "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
                  "paragraph_filter": { "where": [] },
                  "transform": { "kind": "{{kind}}", "pattern": {{Json(pattern)}}, "replacement": "", "template": {{Json(template)}},
                    "instruction": {{Json(instruction)}}, "case_mode": {{Json(caseMode)}}, "sources": {{sources}} } }
                """;
        }

        private static EditProgram Parsed(string raw)
        {
            Assert.True(EditProgramParser.TryParse(raw, out var program, out var error), error);
            return program!;
        }

        [Fact]
        public void TryParse_SetTemplate_SourcesComeFromTheTokens_AndTheArrayIsIgnored()
        {
            var program = Parsed(Plan("chapter_title", "set_template", template: "Chapter {p1}, {p2} {n} {p1}",
                sources: "[\"volume\", \"p5\"]"));

            Assert.True(program.Supported);
            Assert.Equal(["p1", "p2"], program.Transform.Sources.Select(s => s.Name));
        }

        [Fact]
        public void TryParse_SetTemplate_UnknownToken_IsUnsupported()
        {
            var program = Parsed(Plan("chapter_title", "set_template", template: "Chapter {n}: {title}"));

            Assert.False(program.Supported);
            Assert.Equal("Unknown token {title}", program.UnsupportedReason);
        }

        [Theory]
        [InlineData("Chapter { p1 }")]
        [InlineData("Chapter {1}")]
        [InlineData("Chapter {p-1} {}")]
        public void TryParse_SetTemplate_NonIdentifierBraces_StayLiteral(string template)
        {
            var program = Parsed(Plan("chapter_title", "set_template", template: template));

            Assert.True(program.Supported);
            Assert.Empty(program.Transform.Sources);
            Assert.Equal(template, program.Transform.Template);
        }

        [Theory]
        [InlineData("volume_title", "{p1} {old}", "Volume titles can't read paragraphs — rename the chapter first")]
        [InlineData("chapter_title", "{p6}", "p6 is out of reach: sources count only 1 to 5 from the start or end")]
        [InlineData("chapter_title", "{prev}", "Chapter titles can't read prev or next — use p1…p5 or p_end1…p_end5")]
        public void TryParse_SetTemplate_SourceTheTargetCantRead_IsUnsupportedWithTheCatalogueReason(
            string target, string template, string reason)
        {
            var program = Parsed(Plan(target, "set_template", template: template));

            Assert.False(program.Supported);
            Assert.Equal(reason, program.UnsupportedReason);
        }

        [Fact]
        public void TryParse_Llm_ReadsTheSourcesList_AndCatalogueRejectsFirst()
        {
            var program = Parsed(Plan("part_title", "llm", instruction: "use p1", sources: "[\"p1\"]"));

            Assert.False(program.Supported);
            Assert.Equal("Part titles can't read paragraphs — rename the chapter first", program.UnsupportedReason);
        }

        [Fact]
        public void TryParse_Llm_UnknownSourceName_IsUnsupported()
        {
            var program = Parsed(Plan("chapter_title", "llm", instruction: "x", sources: "[\"title\"]"));

            Assert.False(program.Supported);
            Assert.Equal("Unknown source title", program.UnsupportedReason);
        }

        [Fact]
        public void TryParse_Llm_WithSources_IsSupportedAndKeepsThemInOrder()
        {
            var program = Parsed(Plan("chapter_title", "llm", instruction: "the number in p1 and p2", sources: "[\"p2\", \"p1\"]"));

            Assert.True(program.Supported, program.UnsupportedReason);
            Assert.Equal(["p2", "p1"], program.Transform.Sources.Select(s => s.Name));
        }

        [Fact]
        public void TryParse_Llm_WithoutSources_IsSupported()
        {
            var program = Parsed(Plan("chapter_title", "llm", instruction: "fix"));

            Assert.True(program.Supported);
            Assert.Empty(program.Transform.Sources);
        }

        [Theory]
        [InlineData("regex_replace", "x", null)]
        [InlineData("change_case", null, "upper")]
        public void TryParse_RegexAndChangeCase_IgnoreSources(string kind, string? pattern, string? caseMode)
        {
            var program = Parsed(Plan("volume_title", kind, pattern: pattern, caseMode: caseMode, sources: "[\"p1\", \"prev\"]"));

            Assert.True(program.Supported);
            Assert.Empty(program.Transform.Sources);
        }

        [Fact]
        public void TryParse_MissingSourcesField_ReadsNone()
        {
            var program = Parsed(ValidProgram);

            Assert.True(program.Supported);
            Assert.Empty(program.Transform.Sources);
        }

        [Fact]
        public void TryParse_SourceRejection_KeepsTheReasoning()
        {
            var program = Parsed(Plan("chapter_title", "set_template", template: "{title}"));

            Assert.Equal("r", program.Reasoning);
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
