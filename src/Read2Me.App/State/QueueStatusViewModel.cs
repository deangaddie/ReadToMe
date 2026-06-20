using System;

namespace Read2Me.App.State
{
    /// <summary>
    /// Pure view-model for the queue status bars (Character Queue / Audio Queue).
    /// Owns the shared label/ETA formatting that was previously duplicated inline
    /// in both Razor status-bar components. Component-specific chrome (icon, the
    /// "Processing" vs "Generating audio" verb, expand/stream controls) stays in
    /// the Razor; this only formats the numbers both bars render identically.
    /// </summary>
    public readonly struct QueueStatusViewModel
    {
        public bool IsActive { get; }
        public bool ShowProcessing { get; }
        public string ElapsedText { get; }        // "12s" — current item elapsed
        public bool ShowQueued { get; }
        public string QueuedText { get; }          // "5 queued"
        public bool ShowAverage { get; }
        public string AverageText { get; }         // "avg 3.2s/item"
        public bool ShowEta { get; }
        public string EtaText { get; }             // "ETA 1m 20s"
        public bool ShowCompleted { get; }
        public string CompletedText { get; }       // "8 done"

        private QueueStatusViewModel(
            bool isActive, bool showProcessing, string elapsedText,
            bool showQueued, string queuedText,
            bool showAverage, string averageText,
            bool showEta, string etaText,
            bool showCompleted, string completedText)
        {
            IsActive = isActive;
            ShowProcessing = showProcessing;
            ElapsedText = elapsedText;
            ShowQueued = showQueued;
            QueuedText = queuedText;
            ShowAverage = showAverage;
            AverageText = averageText;
            ShowEta = showEta;
            EtaText = etaText;
            ShowCompleted = showCompleted;
            CompletedText = completedText;
        }

        /// <param name="averageUnit">Unit suffix for the average label, e.g. "item" or "para".</param>
        public static QueueStatusViewModel For(
            int queuedCount,
            int processingCount,
            double averageSeconds,
            double estimatedSecondsRemaining,
            int completedCount,
            double currentItemElapsedSeconds,
            string averageUnit)
        {
            var showProcessing = processingCount > 0;
            var showAverage = averageSeconds > 0;
            var showEta = estimatedSecondsRemaining > 0;
            var showQueued = queuedCount > 0;
            var showCompleted = completedCount > 0;

            return new QueueStatusViewModel(
                isActive: queuedCount > 0 || processingCount > 0,
                showProcessing: showProcessing,
                elapsedText: showProcessing
                    ? $"{currentItemElapsedSeconds.ToString("F0")}s"
                    : string.Empty,
                showQueued: showQueued,
                queuedText: showQueued ? $"{queuedCount} queued" : string.Empty,
                showAverage: showAverage,
                averageText: showAverage
                    ? $"avg {averageSeconds.ToString("F1")}s/{averageUnit}"
                    : string.Empty,
                showEta: showEta,
                etaText: showEta ? $"ETA {FormatEta(estimatedSecondsRemaining)}" : string.Empty,
                showCompleted: showCompleted,
                completedText: showCompleted ? $"{completedCount} done" : string.Empty);
        }

        private static string FormatEta(double seconds)
        {
            var eta = TimeSpan.FromSeconds(seconds);
            return eta.TotalHours >= 1
                ? $"{(int)eta.TotalHours}h {eta.Minutes:D2}m"
                : $"{eta.Minutes}m {eta.Seconds:D2}s";
        }
    }
}
