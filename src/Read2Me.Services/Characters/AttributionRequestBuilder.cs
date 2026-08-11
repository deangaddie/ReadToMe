using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Data;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Everything a chunk needs to be asked, or the reasons it cannot be. The builder loads context
    /// once, classifies each requested paragraph into one of three bins, and — when at least one is
    /// askable — renders a single <see cref="LlmRunRequest"/> whose shape (single vs batch) is chosen
    /// by <see cref="Included"/> count.
    /// <list type="bullet">
    /// <item><see cref="Request"/> / <see cref="Parser"/> are null when nothing is askable
    /// (<see cref="Included"/> empty).</item>
    /// <item><see cref="Included"/> — paragraphs carried in the prompt, index-aligned with
    /// <see cref="QueryItems"/>; the parser returns a result per index
    /// 0..Included.Count-1.</item>
    /// <item><see cref="QueryItems"/> — for each included paragraph, the items behind the indices the
    /// prompt numbered, in the same order, recorded at ask time. This is the only mapping from an
    /// answered index back to an item: the queue is asynchronous, and re-resolving positionally at
    /// apply time would stamp the wrong item after a user edit. Classification reads the same list
    /// for which indices are answerable and for the narration text an unlisted name is attested
    /// against.</item>
    /// <item><see cref="Unaskable"/> — blank/whitespace text or no content item; resolve to Unknown
    /// with no LLM call.</item>
    /// <item><see cref="Deferred"/> — trimmed off the leading run by the context reader; re-enqueue.</item>
    /// <item><see cref="Characters"/> — the roster the prompt was built from, so a later reconcile
    /// compares against provably that set (D11: fetched once per chunk).</item>
    /// <item><see cref="Narrator"/> — who narrates this book, carried beside the roster because every
    /// site that judges a speaker string needs both: the narrator token is a wire alias of the linked
    /// character, and means nothing when unlinked.</item>
    /// </list>
    /// </summary>
    internal sealed record ChunkRequest(
        LlmRunRequest? Request,
        TryParse<IReadOnlyDictionary<int, ItemAttributionResult>>? Parser,
        IReadOnlyList<QueuedParagraph> Included,
        IReadOnlyList<QueuedParagraph> Unaskable,
        IReadOnlyList<QueuedParagraph> Deferred,
        IReadOnlyList<Data.Entities.Character> Characters,
        NarratorIdentity Narrator,
        IReadOnlyList<IReadOnlyList<ContextItem>> QueryItems);

    /// <summary>
    /// Builds one attribution <see cref="LlmRunRequest"/> for a chunk of paragraphs — the single seam
    /// that owns everything from context load to a ready-to-run request. Concrete (no interface: a lone
    /// adapter would be a hypothetical seam), pure data-in/data-out, unit-tested directly.
    /// The <see cref="IBookContentReader.GetParagraphBatchContextAsync">batch reader is universal</see>:
    /// both readers window on the same <c>HasContentItem</c> filter, so with one id the batch reader is
    /// equivalent to the single reader and its deferral loop cannot trigger (D5). Template and answer
    /// shape then key on the included count <em>after</em> the load (D1): 1 → single object-shaped
    /// prompt, &gt;1 → batch array-shaped prompt.
    /// </summary>
    internal sealed class AttributionRequestBuilder(LlmPromptService prompts, IProjectReader reader)
    {
        /// <summary>
        /// Loads context for the whole chunk, bins each requested paragraph, and — when at least one
        /// is askable — renders the request. Order matches the spec: context load, three-bin
        /// pre-filter (with the missing-first retry loop), roster fetch once, template/shape by
        /// included count, token budget on every request.
        /// </summary>
        public async Task<ChunkRequest> Build(IReadOnlyList<QueuedParagraph> chunk, ChainStepOptions opts)
        {
            var (before, after) = await prompts.GetContextWindowAsync();

            var byId = chunk.ToDictionary(c => c.ParagraphId);
            var first = chunk[0];
            var unaskable = new List<QueuedParagraph>();

            // The batch reader returns null when the *first* requested id has no content item (or is
            // missing): mark it Unaskable and re-ask for the rest, until the context is non-null or
            // nothing is left. A chunk of 1 collapses to a single null → single Unaskable, with no
            // request — the old "blank/missing single" path, now uniform.
            var remaining = chunk.Select(c => c.ParagraphId).ToList();
            ParagraphBatchContext? ctx = null;
            while (remaining.Count > 0)
            {
                ctx = await reader.GetParagraphBatchContextAsync(
                    first.Folder, first.ChapterId, remaining, before, after);
                if (ctx != null)
                    break;
                unaskable.Add(byId[remaining[0]]);
                remaining = [.. remaining.Skip(1)];
            }

            if (ctx == null)
                return NothingAskable(unaskable, []);

            var deferred = ctx.DeferredIds.Select(id => byId[id]).ToList();

            // Walk the flat entry span, renumbering the surviving targets to a contiguous 0..n-1 so
            // the answer's indices line up. A target whose own text is blank/whitespace is demoted to
            // a context entry (its neighbours keep their positions) and binned Unaskable.
            var included = new List<QueuedParagraph>();
            var queryTexts = new List<string>();
            var queryItems = new List<IReadOnlyList<ContextItem>>();
            var rendered = new List<BatchContextEntry>();
            var nextIndex = 0;
            foreach (var e in ctx.Entries)
            {
                if (e.TargetIndex is not { } ti)
                {
                    rendered.Add(e);
                    continue;
                }

                var item = byId[ctx.IncludedIds[ti]];
                if (string.IsNullOrWhiteSpace(e.Text))
                {
                    unaskable.Add(item);
                    rendered.Add(e with { TargetIndex = null });
                    continue;
                }

                included.Add(item);
                queryTexts.Add(e.Text);
                // Recorded here and nowhere else: the prompt numbers these same items 0..n-1, so
                // position i of this list is the item the answer's index i names (spec §1).
                queryItems.Add(e.Items);
                rendered.Add(e with { TargetIndex = nextIndex++ });
            }

            if (included.Count == 0)
                return NothingAskable(unaskable, deferred);

            // Roster once per chunk (D11): the anonymous {name, aliases} projection lives here alone,
            // and the roster travels back on the result so no later stage refetches it.
            var project = await reader.GetProjectAsync(first.Folder);
            var characters = await reader.GetCharactersWithAliasesAsync(first.Folder);
            // Beside the roster, once per chunk: every judge of a speaker string downstream needs the
            // link to read the narrator token. It is its own read rather than a field off the Project
            // above because ADR-0004 makes NarratorIdentity the only reader of the raw column.
            var narrator = await reader.GetNarratorAsync(first.Folder);
            var rosterJson = JsonSerializer.Serialize(
                characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() }));
            var narratorIdentity = narrator.IsLinked
                ? NarratorPromptText.IdentityParagraph(narrator.DisplayName)
                : string.Empty;

            string RenderPrompt(string template, string contextJson, string responseFormat) =>
                PromptTemplates.Render(template, new Dictionary<string, string>
                {
                    [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                    [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                    [PromptTemplates.KnownCharacters] = rosterJson,
                    [PromptTemplates.ContextJson]     = contextJson,
                    [PromptTemplates.ResponseFormat]  = responseFormat,
                    [PromptTemplates.NarratorIdentity] = narratorIdentity,
                });

            // D2: the budget applies to every ask, any chunk size — a floor over the config, grown to
            // fit the passage. An unset config stays null (the server's own limit).
            var maxTokens = AttributionTokenBudget.ForPassage(opts.Config.MaxTokens, queryTexts);
            var overrides = new LlmRunOverrides(MaxTokens: maxTokens, Temperature: opts.TemperatureOverride);

            LlmRunRequest request;
            TryParse<IReadOnlyDictionary<int, ItemAttributionResult>> parser;
            if (included.Count == 1)
            {
                var template = await prompts.GetCharacterPromptAsync(opts.EffectiveStyle);
                var prompt = RenderPrompt(template,
                    PromptTemplates.BuildContextJson(ToSingleContext(rendered)),
                    ItemAttributionSchema.JsonExample);
                request = new LlmRunRequest(opts.Config, prompt, included[0].Preview,
                    ItemAttributionSchema.JsonSchema, CompletionShape.Object,
                    DisableThinking: !opts.Thinking, Overrides: overrides);
                parser = ParseSingle;
            }
            else
            {
                var renderedCtx = new ParagraphBatchContext(
                    rendered, [.. included.Select(i => i.ParagraphId)], ctx.DeferredIds);
                var template = await prompts.GetBatchCharacterPromptAsync(opts.EffectiveStyle);
                var prompt = RenderPrompt(template,
                    PromptTemplates.BuildBatchContextJson(renderedCtx),
                    ItemBatchAttributionSchema.JsonExample);
                request = new LlmRunRequest(opts.Config,
                    prompt, $"{included.Count} paragraphs: {included[0].Preview}",
                    ItemBatchAttributionSchema.JsonSchema, CompletionShape.Array,
                    DisableThinking: !opts.Thinking, Overrides: overrides);
                parser = ParseBatch(included.Count);
            }

            return new ChunkRequest(
                request, parser, included, unaskable, deferred, characters, narrator,
                queryItems);
        }

        /// <summary>
        /// A chunk with no askable target: no request, no parser, no roster fetch — and so no narrator
        /// fetch either. Nothing downstream judges a speaker, so <c>Unlinked</c> is never read.
        /// </summary>
        private static ChunkRequest NothingAskable(
            IReadOnlyList<QueuedParagraph> unaskable, IReadOnlyList<QueuedParagraph> deferred) =>
            new(null, null, [], unaskable, deferred, [], NarratorIdentity.Unlinked, []);

        /// <summary>
        /// The entries→single-context adapter: rebuilds the flat target-of-one span into the single
        /// prompt's shape — the target as the query, its neighbours as segmented context.
        /// </summary>
        private static ParagraphContext ToSingleContext(IReadOnlyList<BatchContextEntry> entries)
        {
            var pos = 0;
            while (entries[pos].TargetIndex is null)
                pos++;
            var target = entries[pos];
            var preceding = entries.Take(pos).Select(ToContextParagraph).ToList();
            var following = entries.Skip(pos + 1).Select(ToContextParagraph).ToList();
            return new ParagraphContext(new ContextParagraph(target.Text, target.Items), preceding, following);
        }

        private static ContextParagraph ToContextParagraph(BatchContextEntry e) => new(e.Text, e.Items);

        /// <summary>Single answer, wrapped to the batch-shaped index→result map so one classify loop serves both.</summary>
        private static bool ParseSingle(
            string raw, out IReadOnlyDictionary<int, ItemAttributionResult>? parsed, out string? error)
        {
            if (ItemAttributionParser.TryParse(raw, out var p))
            {
                parsed = new Dictionary<int, ItemAttributionResult> { [0] = p };
                error = null;
                return true;
            }
            parsed = null;
            error = "Could not parse LLM response.";
            return false;
        }

        /// <summary>
        /// Batch parser over the contiguous requested indices; a missing one rejects the whole answer
        /// (escalation's unit is the paragraph, and a half-answered batch is not one).
        /// </summary>
        private static TryParse<IReadOnlyDictionary<int, ItemAttributionResult>> ParseBatch(int count)
        {
            var requested = Enumerable.Range(0, count).ToList();
            return (string raw, out IReadOnlyDictionary<int, ItemAttributionResult>? parsed, out string? error) =>
            {
                if (ItemAttributionParser.TryParseBatch(raw, requested, out var p))
                {
                    parsed = p;
                    error = null;
                    return true;
                }
                parsed = null;
                error = "Could not parse batch LLM response.";
                return false;
            };
        }
    }
}
