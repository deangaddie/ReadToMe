using Read2Me.Core.Models;

namespace Read2Me.App.Api
{
    /// <summary>
    /// <c>POST /api/projects/{folder}/import/manual</c> body: how to re-split the source file by hand
    /// (the Blazor Manual Reread dialog's form, on the wire). <c>volume</c> is read only when
    /// <c>hasMultipleVolumes</c>, <c>part</c> only when <c>hasMultipleParts</c>; <c>chapter</c> always.
    /// </summary>
    public sealed record ManualImportRequest(
        bool HasMultipleVolumes,
        bool HasMultipleParts,
        SplitRuleRequest? Volume,
        SplitRuleRequest? Part,
        SplitRuleRequest? Chapter)
    {
        /// <summary>
        /// The same rules the Blazor form applies before it submits: a level that is switched on
        /// needs a rule, and a prefix rule needs a prefix. Anything else is a 400, not a reader error.
        /// </summary>
        public bool TryToOptions(out ManualReadOptions? options, out string? error)
        {
            options = null;
            error = null;

            SectionSplitRule? volume = null, part = null;
            if (HasMultipleVolumes && !TryRule("Volume", Volume, out volume, out error)) return false;
            if (HasMultipleParts && !TryRule("Part", Part, out part, out error)) return false;
            if (!TryRule("Chapter", Chapter, out var chapter, out error)) return false;

            options = new ManualReadOptions(HasMultipleVolumes, HasMultipleParts, volume, part, chapter!);
            return true;
        }

        private static bool TryRule(string level, SplitRuleRequest? rule, out SectionSplitRule? result, out string? error)
        {
            result = null;
            error = null;
            if (rule is null)
            {
                error = $"{level} detection is required.";
                return false;
            }
            if (!SplitRuleRequest.TryParseMode(rule.Mode, out var mode))
            {
                error = $"{level} detection mode must be one of {SplitRuleRequest.ModeNames}.";
                return false;
            }
            var prefix = rule.Prefix?.Trim();
            if (mode == SplitDetectionMode.Prefix && string.IsNullOrEmpty(prefix))
            {
                error = $"{level} prefix cannot be empty.";
                return false;
            }
            result = new SectionSplitRule(mode, mode == SplitDetectionMode.Prefix ? prefix : null);
            return true;
        }
    }

    /// <summary>One level's split rule. <c>mode</c> is <c>Prefix</c>, <c>Arabic</c> or <c>Roman</c>.</summary>
    public sealed record SplitRuleRequest(string? Mode, string? Prefix = null)
    {
        public const string ModeNames = "Prefix, Arabic, Roman";

        /// <summary>Case-insensitive.</summary>
        public static bool TryParseMode(string? text, out SplitDetectionMode mode)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "prefix": mode = SplitDetectionMode.Prefix; return true;
                case "arabic": mode = SplitDetectionMode.Number; return true;
                case "roman": mode = SplitDetectionMode.RomanNumeral; return true;
                default: mode = default; return false;
            }
        }
    }
}
