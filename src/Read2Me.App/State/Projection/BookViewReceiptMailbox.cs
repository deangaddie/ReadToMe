using System;
using System.Collections.Generic;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.App.State.Projection
{
    /// <summary>
    /// One coalesced reconciliation: everything the mailbox held, stated as the newest revision it
    /// knows about and the union of the effects that produced it.
    /// </summary>
    /// <param name="Structural">
    /// Whether any receipt in the batch actually reported a structural effect — the fact the notice
    /// rule turns on. It is carried apart from <paramref name="Effects"/> because an overflowing
    /// batch degrades those to <see cref="BookMutationEffects.Unknown"/>, which claims every facet:
    /// reconciling from that is safe, but announcing from it would tell a reader that a queue's
    /// hundredth attribution had restructured their Book.
    /// </param>
    internal readonly record struct PendingReconciliation(
        ProjectFolderId FolderId, long Revision, BookMutationEffects Effects, bool Structural);

    /// <summary>
    /// A projection's bounded inbox for the receipts other circuits' commits publish (ADR 0007).
    /// <para>
    /// It owns which Book it is taking receipts for, because that question is asked on the
    /// publisher's thread and answered on the circuit's: keeping the binding under the same lock as
    /// the pending batch is what makes "rebound, so forget the old Book's receipts" one atomic step
    /// rather than two fields racing.
    /// </para>
    /// <para>
    /// <see cref="TryTake"/> is a few field writes under that lock and never waits, because it runs
    /// on another producer's commit path: a slow or failing reader must not be able to slow — let
    /// alone fail — someone else's committed mutation. All the work of reconciling happens later, on
    /// the projection's own pump.
    /// </para>
    /// <para>
    /// What accumulates is coalesced rather than queued: a burst of receipts becomes one
    /// reconciliation at the newest revision, since a projection rereads the persisted Book and the
    /// intermediate states are not worth a read each. Past <see cref="Capacity"/> the detail itself
    /// is dropped — the batch collapses to <see cref="BookMutationEffects.Unknown"/>, which is the
    /// safe answer: whole-project scope, every facet, expansion continuity lost but correctness
    /// kept. Bounded memory therefore costs fine detail, never accuracy.
    /// </para>
    /// </summary>
    internal sealed class BookViewReceiptMailbox
    {
        /// <summary>
        /// How many receipts are kept in detail before the batch collapses. Large enough that an
        /// ordinary burst — a queue working through a chapter — still carries its identifiers and
        /// split/merge relationships; small enough that a runaway producer cannot grow this without
        /// bound while a projection is busy building.
        /// </summary>
        public const int Capacity = 64;

        private readonly object _gate = new();

        private ProjectFolderId? _folderId;
        private BookMutationEffects? _coalesced;
        private long _revision;
        private int _count;
        private bool _overflowed;
        private bool _structural;

        /// <summary>
        /// Starts taking receipts for one Book. Anything pending for the Book being left is dropped:
        /// it describes a Book this reader is no longer showing. Rebinding to the Book already bound
        /// changes nothing, so re-opening the same project does not lose a receipt that arrived while
        /// it was being read.
        /// </summary>
        public void BindTo(ProjectFolderId folderId)
        {
            lock (_gate)
            {
                if (_folderId == folderId) return;
                _folderId = folderId;
                ResetPending();
            }
        }

        /// <summary>Stops taking receipts for good — the reader this mailbox serves has gone.</summary>
        public void Close()
        {
            lock (_gate)
            {
                _folderId = null;
                ResetPending();
            }
        }

        /// <summary>
        /// Takes one receipt, or refuses it as belonging to another Book. Never throws and never
        /// waits: it is called from the publisher's thread, inside another producer's commit path.
        /// </summary>
        /// <returns>True if the receipt was taken and the pump has something to do.</returns>
        public bool TryTake(BookMutationReceipt receipt)
        {
            lock (_gate)
            {
                if (_folderId != receipt.FolderId) return false;

                _revision = Math.Max(_revision, receipt.Revision);
                _count++;
                // Kept whatever happens to the detail: one bool costs nothing to carry past the
                // bound, and it is the only thing the notice rule needs to stay honest.
                _structural |= receipt.Effects.Facets.HasFlag(BookFacets.Structure);

                if (_overflowed) return true;

                if (_count > Capacity)
                {
                    // Past the bound, one marker stands for everything: rebuild the whole project at
                    // the newest revision. Detail is what is discarded, not the update itself.
                    _overflowed = true;
                    _coalesced = BookMutationEffects.Unknown;
                    return true;
                }

                _coalesced = _coalesced is { } already ? Merge(already, receipt.Effects) : receipt.Effects;
                return true;
            }
        }

        /// <summary>
        /// Takes everything pending as one reconciliation, leaving the mailbox empty. Null when
        /// nothing is pending — which is the common case for the pass that follows a coalesced burst.
        /// </summary>
        public PendingReconciliation? Drain()
        {
            lock (_gate)
            {
                if (_folderId is not { } folderId || _coalesced is not { } effects) return null;

                var pending = new PendingReconciliation(folderId, _revision, effects, _structural);
                ResetPending();
                return pending;
            }
        }

        private void ResetPending()
        {
            _coalesced = null;
            _revision = 0;
            _count = 0;
            _overflowed = false;
            _structural = false;
        }

        /// <summary>
        /// Two commits' effects as one. Scope degrades to <see cref="BookMutationScope.WholeProject"/>
        /// if either side could not enumerate what it touched, facets union, and the identifiers and
        /// relationships concatenate in the order they were committed — a split of a node produced by
        /// an earlier split only carries expansion correctly if it is applied after it.
        /// </summary>
        private static BookMutationEffects Merge(BookMutationEffects first, BookMutationEffects second) =>
            new()
            {
                Scope = first.Scope == BookMutationScope.WholeProject || second.Scope == BookMutationScope.WholeProject
                    ? BookMutationScope.WholeProject
                    : BookMutationScope.Exact,
                Facets = first.Facets | second.Facets,
                // Deliberately dropped: one coalesced batch created no single thing, so there is no
                // one identity to name.
                CreatedId = null,
                NodeIds = Concat(first.NodeIds, second.NodeIds),
                ParagraphIds = Concat(first.ParagraphIds, second.ParagraphIds),
                ParagraphItemIds = Concat(first.ParagraphItemIds, second.ParagraphItemIds),
                Structural = Concat(first.Structural, second.Structural),
            };

        private static IReadOnlyList<T> Concat<T>(IReadOnlyList<T> first, IReadOnlyList<T> second)
        {
            if (first.Count == 0) return second;
            if (second.Count == 0) return first;

            var merged = new List<T>(first.Count + second.Count);
            merged.AddRange(first);
            merged.AddRange(second);
            return merged;
        }
    }
}
