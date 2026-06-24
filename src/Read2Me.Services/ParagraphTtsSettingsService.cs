using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    /// <summary>
    /// CRUD + active-selection for paragraph-TTS service configurations.
    /// </summary>
    public class ParagraphTtsSettingsService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<ParagraphTtsSettingsService> _logger;
        private readonly ServiceConfigStore<ParagraphTtsServiceConfig> _store;

        public event Action? OnChanged;

        public ParagraphTtsSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<ParagraphTtsSettingsService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _store = new ServiceConfigStore<ParagraphTtsServiceConfig>(
                dbFactory, logger,
                db => db.ParagraphTtsServiceConfigs,
                s => s.ActiveParagraphTtsConfigId,
                (s, id) => s.ActiveParagraphTtsConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "ParagraphTts");
            _store.OnChanged += () => OnChanged?.Invoke();
        }

        public async Task<List<ParagraphTtsServiceConfig>> GetAllConfigsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ParagraphTtsServiceConfigs
                .Include(c => c.SubstitutionSteps)
                .Include(c => c.ToSentenceCaseConfig)
                .OrderBy(c => c.Name)
                .ToListAsync();
        }
        public Task<int?> GetActiveConfigIdAsync() => _store.GetActiveConfigIdAsync();
        public virtual Task<ParagraphTtsServiceConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<ParagraphTtsServiceConfig> CreateConfigAsync(ParagraphTtsServiceConfig config) =>
            _store.CreateConfigAsync(config);

        public async Task UpdateConfigAsync(ParagraphTtsServiceConfig config)
        {
            _logger.LogInformation("Updating ParagraphTts config (ID {Id})", config.Id);
            await using var db = await _dbFactory.CreateDbContextAsync();

            var existing = await db.ParagraphTtsServiceConfigs
                .Include(c => c.SubstitutionSteps)
                .Include(c => c.ToSentenceCaseConfig)
                .SingleAsync(c => c.Id == config.Id);

            existing.Name = config.Name;
            existing.Type = config.Type;
            existing.SettingsJson = config.SettingsJson;
            existing.EnabledStepIds = config.EnabledStepIds;

            var incomingIds = config.SubstitutionSteps.Select(s => s.Id).ToHashSet();
            var existingIds = existing.SubstitutionSteps.Select(s => s.Id).ToHashSet();

            foreach (var removed in existing.SubstitutionSteps.Where(s => !incomingIds.Contains(s.Id)).ToList())
                db.TextSubstitutionSteps.Remove(removed);

            foreach (var incoming in config.SubstitutionSteps)
            {
                if (existingIds.Contains(incoming.Id))
                {
                    var row = existing.SubstitutionSteps.Single(s => s.Id == incoming.Id);
                    row.FromText = incoming.FromText;
                    row.ToText = incoming.ToText;
                    row.Order = incoming.Order;
                }
                else
                {
                    incoming.ParagraphTtsServiceConfigId = config.Id;
                    db.TextSubstitutionSteps.Add(incoming);
                }
            }

            if (config.ToSentenceCaseConfig is { } incomingTsc)
            {
                if (existing.ToSentenceCaseConfig is null)
                {
                    incomingTsc.ParagraphTtsServiceConfigId = config.Id;
                    db.ToSentenceCaseConfigs.Add(incomingTsc);
                }
                else
                {
                    existing.ToSentenceCaseConfig.ParagraphEnabled = incomingTsc.ParagraphEnabled;
                    existing.ToSentenceCaseConfig.WordEnabled = incomingTsc.WordEnabled;
                    existing.ToSentenceCaseConfig.WordMinLength = incomingTsc.WordMinLength;
                }
            }
            else if (existing.ToSentenceCaseConfig is not null)
            {
                db.ToSentenceCaseConfigs.Remove(existing.ToSentenceCaseConfig);
            }

            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public Task DeleteConfigAsync(int configId) => _store.DeleteConfigAsync(configId);
    }
}
