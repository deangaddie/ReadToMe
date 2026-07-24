namespace Read2Me.Services.Characters
{
    internal static class AttributionStatusExtensions
    {
        /// <summary>
        /// True when the status means the ask never reached a usable answer for infrastructure
        /// reasons (the server was unreachable, or the call itself failed) rather than because the
        /// model answered poorly. Infra failures are not evidence a config is too weak, so the walk
        /// carries the item on instead of treating the rung as having decided it.
        /// </summary>
        public static bool IsInfraFailure(this AttributionStatus status) =>
            status is AttributionStatus.ServiceUnavailable or AttributionStatus.Failed;
    }
}
