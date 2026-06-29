using Read2Me.Services.Audio.Assembly;

namespace Read2Me.App.State
{
    /// <summary>
    /// Pure view-model for the Assemble button and assembly status bar.
    /// Given precondition counts + runtime state, derives enabled/disabled + display text.
    /// </summary>
    public readonly struct AssemblyButtonViewModel
    {
        public bool IsEnabled { get; }
        public string? DisabledReason { get; }
        public string PhaseLabel { get; }
        public string EncodePercentText { get; }

        private AssemblyButtonViewModel(
            bool isEnabled,
            string? disabledReason,
            string phaseLabel,
            string encodePercentText)
        {
            IsEnabled = isEnabled;
            DisabledReason = disabledReason;
            PhaseLabel = phaseLabel;
            EncodePercentText = encodePercentText;
        }

        /// <param name="audioRemaining">Items still missing audio (from AudiobookAssemblyService.AudioRemainingCount or node-status).</param>
        /// <param name="audioQueueBusy">True when the Audio Queue has queued or processing items for this project.</param>
        /// <param name="isRunning">True while an assembly job is in flight.</param>
        /// <param name="currentPhase">Current assembly phase, or null when idle.</param>
        /// <param name="encodePercent">0–1 encode progress fraction.</param>
        public static AssemblyButtonViewModel For(
            int audioRemaining,
            bool audioQueueBusy,
            bool isRunning,
            AssemblyPhase? currentPhase,
            double encodePercent)
        {
            string? disabledReason = null;
            bool isEnabled = true;

            if (audioQueueBusy)
            {
                isEnabled = false;
                disabledReason = "Audio queue is busy";
            }
            else if (isRunning)
            {
                isEnabled = false;
                disabledReason = null; // spinner shown instead
            }

            var phaseLabel = currentPhase switch
            {
                AssemblyPhase.Gather => "Gathering",
                AssemblyPhase.Silence => "Generating silence",
                AssemblyPhase.ProbeConcat => "Building",
                AssemblyPhase.Encode => "Encoding",
                AssemblyPhase.Finalize => "Finalizing",
                _ => string.Empty,
            };

            var percentText = currentPhase == AssemblyPhase.Encode
                ? $"{(int)(encodePercent * 100)}%"
                : string.Empty;

            return new AssemblyButtonViewModel(isEnabled, disabledReason, phaseLabel, percentText);
        }
    }
}
