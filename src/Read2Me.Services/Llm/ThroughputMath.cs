namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The one place tokens and milliseconds become a tok/s figure. Every throughput number in the
    /// app — a request's, a config's, a run's — divides here, so they cannot drift apart (ADR 0003).
    /// </summary>
    internal static class ThroughputMath
    {
        /// <summary>
        /// Tokens per second from <c>predicted_n</c> and <c>predicted_ms</c>, or null when either
        /// input is absent or no time actually elapsed. Never divides by zero, and never reports
        /// <c>0</c> to mean "unknown" — absence and zero are different answers.
        /// </summary>
        public static double? Rate(int? tokens, double? milliseconds) =>
            tokens is { } n && milliseconds is { } ms && ms > 0
                ? n / ms * 1000.0
                : null;
    }
}
