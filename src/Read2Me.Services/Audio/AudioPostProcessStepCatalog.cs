namespace Read2Me.Services.Audio
{
    /// <summary>An enabled step paired with its stored settings payload.</summary>
    public sealed record EnabledPostProcessStep(IAudioPostProcessStep Step, string? SettingsJson);

    /// <summary>
    /// Maps stored step configs to registered <see cref="IAudioPostProcessStep"/> instances.
    /// Mirrors <see cref="Text.ITextProcessingStepCatalog"/> for the audio side.
    /// </summary>
    public interface IAudioPostProcessStepCatalog
    {
        /// <summary>Enabled steps in stored order; configs with no registered step are skipped.</summary>
        Task<IReadOnlyList<EnabledPostProcessStep>> GetEnabledStepsAsync();
    }

    public class AudioPostProcessStepCatalog(
        IEnumerable<IAudioPostProcessStep> steps,
        AudioProcessingSettingsService settingsService) : IAudioPostProcessStepCatalog
    {
        public async Task<IReadOnlyList<EnabledPostProcessStep>> GetEnabledStepsAsync()
        {
            var byId = steps.GroupBy(s => s.StepId).ToDictionary(g => g.Key, g => g.First());
            var configs = await settingsService.GetPostProcessStepsAsync();
            return configs
                .Where(c => c.Enabled && byId.ContainsKey(c.StepId))
                .Select(c => new EnabledPostProcessStep(byId[c.StepId], c.SettingsJson))
                .ToList();
        }
    }
}
