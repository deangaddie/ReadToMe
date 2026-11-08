namespace Read2Me.Services.BookEdits
{
    /// <summary>The packed requests: batches of positions into the packed list, and the positions too long to send.</summary>
    public sealed record BatchPlan(IReadOnlyList<IReadOnlyList<int>> Batches, IReadOnlyList<int> TooLong);

    /// <summary>
    /// The one packing rule for AI edit requests, used by the run and by the plan's request count:
    /// a request holds up to <see cref="MaxItems"/> items and about <see cref="CharBudget"/> characters of
    /// text plus sources. An item over budget goes alone (sources are never truncated); one over
    /// <see cref="MaxItemChars"/> is too long to send at all.
    /// </summary>
    public static class BatchPacker
    {
        public const int MaxItems = 8;
        public const int CharBudget = 12_000;
        public const int MaxItemChars = 60_000;

        public const string TooLongMessage = "Too long to send to the AI (text and sources)";

        /// <summary>Characters an item adds to a request: its text plus its source values.</summary>
        public static int Size(EditTarget target) =>
            target.CurrentValue.Length + target.Sources.Sum(s => s.Value?.Length ?? 0);

        /// <summary>Why a target fails before any request (a missing or self source, too long to send), or null.</summary>
        public static string? PreflightFailure(EditTarget target) =>
            target.SourceFailure ?? (Size(target) > MaxItemChars ? TooLongMessage : null);

        /// <summary>Packs items in order; positions refer to <paramref name="sizes"/>.</summary>
        public static BatchPlan Pack(IReadOnlyList<int> sizes)
        {
            var batches = new List<IReadOnlyList<int>>();
            var tooLong = new List<int>();
            var current = new List<int>();
            var chars = 0;
            void Flush()
            {
                if (current.Count > 0) batches.Add(current);
                current = [];
                chars = 0;
            }

            for (var i = 0; i < sizes.Count; i++)
            {
                var size = sizes[i];
                if (size > MaxItemChars)
                {
                    tooLong.Add(i);
                    continue;
                }
                if (size > CharBudget)
                {
                    Flush();
                    batches.Add([i]);
                    continue;
                }
                if (current.Count >= MaxItems || chars + size > CharBudget)
                    Flush();
                current.Add(i);
                chars += size;
            }
            Flush();
            return new BatchPlan(batches, tooLong);
        }

        /// <summary>Packs the targets that can be sent (no missing or self source); positions index into <paramref name="targets"/>.</summary>
        public static BatchPlan PlanFor(IReadOnlyList<EditTarget> targets)
        {
            var sendable = Enumerable.Range(0, targets.Count).Where(i => targets[i].SourceFailure == null).ToList();
            var plan = Pack(sendable.Select(i => Size(targets[i])).ToList());
            return new BatchPlan(
                plan.Batches.Select(b => (IReadOnlyList<int>)b.Select(p => sendable[p]).ToList()).ToList(),
                plan.TooLong.Select(p => sendable[p]).ToList());
        }

        /// <summary>Requests an LLM plan sends for these targets.</summary>
        public static int RequestCount(IReadOnlyList<EditTarget> targets) => PlanFor(targets).Batches.Count;
    }
}
