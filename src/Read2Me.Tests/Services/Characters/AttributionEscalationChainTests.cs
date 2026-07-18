using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// Walk-policy tests for <see cref="AttributionEscalationChain"/> driven over a scripted
    /// <see cref="IChainStep"/> fake — no LLM, DB, or reader. Each config's per-item
    /// <see cref="StepOutcome"/> is canned, so these assert the walk's policy alone: the
    /// step-0-vs-steps-1..n split, best-prior fallback, <see cref="EscalationTrigger"/> routing, the
    /// <see cref="AttributionStatus.ModelLoading"/> short-circuit, final-step <c>Accept</c>, the
    /// <see cref="EscalationStarted"/> publish, and the <c>ItemDeferred</c> fire. Step mechanics
    /// (prompt/batch-core/self-consistency/grouping/chunking) live in the fake-runner tests against
    /// <see cref="CharacterAttributionService"/>.RunAsync; the walk+real-step integration lives in
    /// <see cref="CharacterAttributionChainTests"/>.
    /// </summary>
    public class AttributionEscalationChainTests
    {
        private static readonly ProjectFolderId Folder = new("chain-book");
        private static readonly Guid Chapter = Guid.NewGuid();

        private readonly LlmSettingsService _settings = Substitute.For<LlmSettingsService>(null!, null!);

        /// <summary>All items share one chapter, so a config runs as a single group (one step call).</summary>
        private static QueuedParagraph Item(string preview = "P") =>
            new(Folder, Guid.NewGuid(), preview, Chapter, Guid.NewGuid(), Guid.NewGuid());

        private void SetChain(params string[] names) =>
            _settings.GetAttributionChainAsync().Returns(
                names.Select(n => new LlmServerConfig { Name = n, AttributionBatchSize = 8 }).ToList());

        private AttributionEscalationChain Chain(IChainStep step, EventBroadcaster<LlmStreamEvent>? broadcaster = null) =>
            new(step, _settings, broadcaster ?? new EventBroadcaster<LlmStreamEvent>(),
                NullLogger<AttributionEscalationChain>.Instance);

        private static StepOutcome Confident(AttributionStatus status = AttributionStatus.Resolved, string? reason = null) =>
            new(new AttributionOutcome(status, null, reason), EscalationTrigger.None);

        private static StepOutcome Suspect(EscalationTrigger trigger, AttributionStatus status) =>
            new(new AttributionOutcome(status, null, null), trigger);

        private static async Task<List<(QueuedParagraph Item, AttributionOutcome Outcome)>> DrainAsync(
            AttributionEscalationChain chain, IReadOnlyList<QueuedParagraph> queued,
            AttributionQueueCallbacks? callbacks = null)
        {
            var results = new List<(QueuedParagraph, AttributionOutcome)>();
            await foreach (var pair in chain.AttributeQueueAsync(queued, callbacks, CancellationToken.None))
                results.Add(pair);
            return results;
        }

        /// <summary>
        /// Scripted <see cref="IChainStep"/>: yields a canned <see cref="StepOutcome"/> per item for a
        /// config, records the order configs were invoked, and appends run markers to an optional trace
        /// so a test can interleave step invocations with the walk's live yields.
        /// </summary>
        private sealed class ScriptedStep(List<string>? trace = null) : IChainStep
        {
            private readonly Dictionary<string, Func<QueuedParagraph, StepOutcome>> _scripts = new();

            /// <summary>Config names in the order the walk called into them.</summary>
            public List<string> Invocations { get; } = [];

            public ScriptedStep ForConfig(string name, StepOutcome outcome) => ForConfig(name, _ => outcome);

            public ScriptedStep ForConfig(string name, Func<QueuedParagraph, StepOutcome> script)
            {
                _scripts[name] = script;
                return this;
            }

            public async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunAsync(
                IReadOnlyList<QueuedParagraph> items, ChainStepOptions opts,
                AttributionQueueCallbacks? callbacks, [EnumeratorCancellation] CancellationToken ct)
            {
                Invocations.Add(opts.Config.Name);
                trace?.Add($"run:{opts.Config.Name}");
                if (!_scripts.TryGetValue(opts.Config.Name, out var script))
                    throw new InvalidOperationException($"No scripted outcome for config '{opts.Config.Name}'");
                foreach (var item in items)
                {
                    await Task.Yield();
                    yield return (item, script(item));
                }
            }
        }

        // ── single-config chain: no escalation ────────────────────────────────

        [Fact]
        public async Task SingleConfigChain_NoEscalation_EveryItemYieldedFromStep0()
        {
            SetChain("A");
            var step = new ScriptedStep().ForConfig("A", Confident());

            var results = await DrainAsync(Chain(step), [Item(), Item()]);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(AttributionStatus.Resolved, r.Outcome.Status));
            Assert.Equal(["A"], step.Invocations);   // primary only, no escalation config touched
        }

        [Fact]
        public async Task NoConfiguredChain_YieldsNoLlmConfigured_StepNeverRun()
        {
            SetChain();   // empty chain
            var step = new ScriptedStep();

            var results = await DrainAsync(Chain(step), [Item(), Item()]);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(AttributionStatus.NoLlmConfigured, r.Outcome.Status));
            Assert.Empty(step.Invocations);
        }

        [Fact]
        public async Task EmptyQueue_YieldsNothing_NeverConsultsSettings()
        {
            var results = await DrainAsync(Chain(new ScriptedStep()), []);
            Assert.Empty(results);   // returns before the chain lookup, so the unconfigured mock is fine
        }

        // ── suspect escalates; confident step-0 answers surface live ──────────

        [Fact]
        public async Task Suspect_EscalatesToNextConfig_ConfidentStep0YieldedBeforeEscalation()
        {
            SetChain("A", "B");
            var i0 = Item("resolved");
            var i1 = Item("suspect");
            var trace = new List<string>();
            var step = new ScriptedStep(trace)
                .ForConfig("A", it => it.ParagraphId == i0.ParagraphId
                    ? Confident()
                    : Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))
                .ForConfig("B", Confident());

            var seen = new List<string>();
            await foreach (var (item, _) in Chain(step).AttributeQueueAsync([i0, i1], callbacks: null, CancellationToken.None))
            {
                trace.Add(item.ParagraphId == i0.ParagraphId ? "yield:i0" : "yield:i1");
                seen.Add(item.ParagraphId == i0.ParagraphId ? "i0" : "i1");
            }

            // Confident i0 streams live during config A; only then does the suspect i1 escalate to B.
            Assert.Equal(["run:A", "yield:i0", "run:B", "yield:i1"], trace);
            Assert.Equal(["A", "B"], step.Invocations);
            Assert.Equal(["i0", "i1"], seen);
        }

        // ── best-prior fallback on last-entry infra failure ───────────────────

        [Fact]
        public async Task LastEntryInfraFailure_ResolvesFromBestPriorUsableAnswer()
        {
            SetChain("A", "B");
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))  // usable + suspect
                .ForConfig("B", Confident(AttributionStatus.Failed, "B down"));                  // final infra fail

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.Unknown, outcome.Status);   // A's best-prior answer, not Failed
            Assert.Equal("Speaker unknown after escalating through 2 models (A → B)", outcome.FailureReason);
            Assert.Equal(["A", "B"], step.Invocations);
        }

        [Fact]
        public async Task LastEntryInfraFailure_NoUsablePrior_SurfacesTheFailure()
        {
            SetChain("A", "B");
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.ParseFailure, AttributionStatus.Failed))  // suspect, unusable
                .ForConfig("B", Confident(AttributionStatus.Failed, "B down"));                      // final infra fail

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.Failed, outcome.Status);   // nothing usable to fall back to
        }

        [Fact]
        public async Task MidChainInfraFailure_CarriesSuspectToNextConfig_ThenResolves()
        {
            SetChain("A", "B", "C");
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))    // usable, escalates
                .ForConfig("B", Confident(AttributionStatus.ServiceUnavailable, "B down"))        // mid infra → carry on
                .ForConfig("C", Confident());                                                     // final resolves

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal(["A", "B", "C"], step.Invocations);   // B's infra failure skipped ahead, not surfaced
        }

        // ── ModelLoading short-circuit: no escalation, never best-prior ───────

        [Fact]
        public async Task ModelLoading_ShortCircuits_DoesNotEscalate()
        {
            SetChain("A", "B");
            var step = new ScriptedStep().ForConfig("A", Confident(AttributionStatus.ModelLoading, "loading"));

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.ModelLoading, outcome.Status);
            Assert.Equal(["A"], step.Invocations);   // B never reached — escalating would evict the loading model
        }

        [Fact]
        public async Task ModelLoading_AtFinalStep_DoesNotFallBackToBestPrior()
        {
            SetChain("A", "B");
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))   // usable best-prior
                .ForConfig("B", Confident(AttributionStatus.ModelLoading, "loading"));           // final, still loading

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.ModelLoading, outcome.Status);   // retryable, NOT A's Unknown best-prior
        }

        // ── final-step Accept ─────────────────────────────────────────────────

        [Fact]
        public async Task FinalStep_UnlistedName_AcceptedAsResolved_NoReason()
        {
            SetChain("A", "B");
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.UnlistedName, AttributionStatus.Resolved))
                .ForConfig("B", Suspect(EscalationTrigger.UnlistedName, AttributionStatus.Resolved));

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, outcome.Status);   // final accepts the new character
            Assert.Null(outcome.FailureReason);
        }

        [Fact]
        public async Task FinalStep_Unknown_StaysUnknown_WithEscalationReason()
        {
            SetChain("A", "B");
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))
                .ForConfig("B", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown));

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.Unknown, outcome.Status);
            Assert.Equal("Speaker unknown after escalating through 2 models (A → B)", outcome.FailureReason);
        }

        // ── EscalationStarted publish ─────────────────────────────────────────

        [Fact]
        public async Task EscalationStarted_PublishedPerStep_WithFullSuspectCount()
        {
            SetChain("A", "B");
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += e => events.Add(e);

            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))
                .ForConfig("B", Confident());

            await DrainAsync(Chain(step, broadcaster), [Item(), Item()]);

            var escalation = Assert.Single(events.OfType<EscalationStarted>());
            Assert.Equal(1, escalation.Step);
            Assert.Equal("B", escalation.ConfigName);
            Assert.Equal(2, escalation.ItemCount);   // both suspects across the queue
        }

        [Fact]
        public async Task SingleConfigChain_Unknown_CarriesNoEscalationReason()
        {
            SetChain("A");   // no escalation tail
            var step = new ScriptedStep().ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown));

            var outcome = Assert.Single(await DrainAsync(Chain(step), [Item()])).Outcome;

            Assert.Equal(AttributionStatus.Unknown, outcome.Status);
            Assert.Null(outcome.FailureReason);   // DidEscalate false → Accept adds no reason
        }

        [Fact]
        public async Task SingleConfigChain_PublishesNoEscalation()
        {
            SetChain("A");
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += e => events.Add(e);

            var step = new ScriptedStep().ForConfig("A", Confident());

            await DrainAsync(Chain(step, broadcaster), [Item()]);

            Assert.DoesNotContain(events, e => e is EscalationStarted);
        }

        // ── ItemDeferred fire ─────────────────────────────────────────────────

        [Fact]
        public async Task ItemDeferred_FiredForSuspect_LeavingTheInFlightSet()
        {
            SetChain("A", "B");
            var item = Item();
            var step = new ScriptedStep()
                .ForConfig("A", Suspect(EscalationTrigger.Unknown, AttributionStatus.Unknown))
                .ForConfig("B", Confident());

            var deferred = new List<QueuedParagraph>();
            await DrainAsync(Chain(step), [item], new AttributionQueueCallbacks(ItemDeferred: deferred.Add));

            Assert.Equal([item], deferred);   // told exactly once, when A left it suspect for B
        }

        [Fact]
        public async Task ItemDeferred_NotFired_ForConfidentStep0Answer()
        {
            SetChain("A", "B");
            var step = new ScriptedStep().ForConfig("A", Confident());

            var deferred = new List<QueuedParagraph>();
            await DrainAsync(Chain(step), [Item()], new AttributionQueueCallbacks(ItemDeferred: deferred.Add));

            Assert.Empty(deferred);
            Assert.Equal(["A"], step.Invocations);
        }
    }
}
