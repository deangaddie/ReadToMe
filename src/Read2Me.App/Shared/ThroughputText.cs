using Read2Me.Services.Llm;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// How a throughput figure reads on every surface. One place, so the settings page, StatusDock
    /// and the stream view can never disagree about what an unmeasurable rate looks like.
    /// </summary>
    /// <remarks>
    /// <b>Absence is a rendered figure, not a missing one.</b> A null rate prints
    /// <see cref="Absent"/> in the slot the number would have occupied, at reduced opacity — never
    /// <c>0</c>, which would claim the model generated nothing, and never a hidden row, which reads
    /// as a bug (ADR 0003).
    /// </remarks>
    public static class ThroughputText
    {
        /// <summary>What "we could not measure this" looks like, everywhere.</summary>
        public const string Absent = "—";

        /// <summary>Opacity for a figure that is absent rather than small.</summary>
        public const string AbsentStyle = "opacity:0.35;";

        /// <summary>A rate with its unit, e.g. <c>41.2 tok/s</c>, or <c>— tok/s</c> when unmeasured.</summary>
        public static string Rate(double? tokensPerSecond) =>
            tokensPerSecond is { } r ? $"{r:F1} tok/s" : $"{Absent} tok/s";

        /// <summary>A bare rate for a table cell, where the column header already carries the unit.</summary>
        public static string BareRate(double? tokensPerSecond) =>
            tokensPerSecond is { } r ? r.ToString("F1") : Absent;

        /// <summary>A token count, or absence — a request that reported no timings counted no tokens.</summary>
        public static string Count(int? value) => value?.ToString() ?? Absent;

        /// <summary>Greys the slot when there is no figure in it, and leaves it alone when there is.</summary>
        public static string StyleFor(double? value) => value is null ? AbsentStyle : string.Empty;
    }
}
