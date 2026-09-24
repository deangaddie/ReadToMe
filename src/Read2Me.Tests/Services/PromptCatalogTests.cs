using System.Text.RegularExpressions;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// The prompt catalog is what both UIs and the agent API describe a prompt kind with; every kind
    /// must be self-consistent: its default template uses only the tokens it lists, and its sample
    /// values render every listed token.
    /// </summary>
    public class PromptCatalogTests
    {
        private static readonly Regex Token = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

        [Fact]
        public void Catalog_lists_the_eight_kinds_in_page_order()
        {
            Assert.Equal(
                ["character", "batch-character", "simple-character", "simple-batch-character",
                 "voice-plan", "narrator-voice-plan", "discover-characters", "voice"],
                PromptCatalog.Kinds.Select(k => k.Kind).ToArray());
        }

        [Fact]
        public void Find_is_exact_and_null_for_unknown()
        {
            Assert.Equal("voice", PromptCatalog.Find("voice")?.Kind);
            Assert.Null(PromptCatalog.Find("Voice"));
            Assert.Null(PromptCatalog.Find("no-such-kind"));
        }

        public static TheoryData<string> KindNames =>
            new(PromptCatalog.Kinds.Select(k => k.Kind));

        [Theory]
        [MemberData(nameof(KindNames))]
        public void Default_template_uses_only_listed_tokens(string kind)
        {
            var descriptor = PromptCatalog.Find(kind)!;
            var used = Token.Matches(descriptor.DefaultTemplate).Select(m => m.Groups[1].Value).Distinct();
            Assert.All(used, token => Assert.Contains(token, descriptor.Tokens));
        }

        [Theory]
        [MemberData(nameof(KindNames))]
        public void Sample_values_cover_every_listed_token(string kind)
        {
            var descriptor = PromptCatalog.Find(kind)!;
            Assert.All(descriptor.Tokens, token => Assert.True(
                descriptor.SampleValues.TryGetValue(token, out var value) && !string.IsNullOrWhiteSpace(value),
                $"{kind} has no sample value for {{{{{token}}}}}"));
        }

        [Theory]
        [MemberData(nameof(KindNames))]
        public void Rendering_the_default_with_samples_leaves_no_token(string kind)
        {
            var descriptor = PromptCatalog.Find(kind)!;
            var rendered = PromptTemplates.Render(descriptor.DefaultTemplate, descriptor.SampleValues);
            Assert.DoesNotMatch(Token, rendered);
        }

        [Fact]
        public void Only_the_full_attribution_kinds_check_narrator_identity()
        {
            var compatibility = new Read2Me.Services.AttributionPromptCompatibility(true, true);
            var flagged = PromptCatalog.Kinds
                .Where(k => k.MissingNarratorIdentity?.Invoke(compatibility) == true)
                .Select(k => k.Kind);
            Assert.Equal(["character", "batch-character"], flagged);
        }

        [Fact]
        public void Every_kind_has_title_description_and_expected_response_except_voice()
        {
            Assert.All(PromptCatalog.Kinds, k =>
            {
                Assert.False(string.IsNullOrWhiteSpace(k.Title));
                Assert.False(string.IsNullOrWhiteSpace(k.Description));
                Assert.NotEmpty(k.Tokens);
            });
            Assert.Null(PromptCatalog.Find("voice")!.ExpectedResponse);
            Assert.All(PromptCatalog.Kinds.Where(k => k.Kind != "voice"),
                k => Assert.False(string.IsNullOrWhiteSpace(k.ExpectedResponse)));
        }
    }
}
