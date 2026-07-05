using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Services
{
    public class LlmPromptService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<LlmPromptService> _logger;

        public event Action? OnChanged;
        protected virtual void NotifyChanged() => OnChanged?.Invoke();

        public LlmPromptService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<LlmPromptService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<string> GetCharacterPromptAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return string.IsNullOrWhiteSpace(row?.CharacterPrompt)
                ? PromptTemplates.DefaultCharacterPrompt
                : row.CharacterPrompt;
        }

        public async Task<string> GetBatchCharacterPromptAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return string.IsNullOrWhiteSpace(row?.BatchCharacterPrompt)
                ? PromptTemplates.DefaultBatchCharacterPrompt
                : row.BatchCharacterPrompt;
        }

        public virtual async Task<string> GetVoicePromptAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return string.IsNullOrWhiteSpace(row?.VoicePrompt)
                ? PromptTemplates.DefaultVoicePrompt
                : row.VoicePrompt;
        }

        public virtual async Task<string> GetVoicePlanPromptAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return string.IsNullOrWhiteSpace(row?.VoicePlanPrompt)
                ? PromptTemplates.DefaultVoicePlanPrompt
                : row.VoicePlanPrompt;
        }

        public virtual async Task<string> GetNarratorVoicePlanPromptAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return string.IsNullOrWhiteSpace(row?.NarratorVoicePlanPrompt)
                ? PromptTemplates.DefaultNarratorVoicePlanPrompt
                : row.NarratorVoicePlanPrompt;
        }

        public async Task SetCharacterPromptAsync(string template)
        {
            _logger.LogInformation("Saving character prompt template");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.CharacterPrompt = template;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task SetBatchCharacterPromptAsync(string template)
        {
            _logger.LogInformation("Saving batch character prompt template");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.BatchCharacterPrompt = template;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task SetVoicePromptAsync(string template)
        {
            _logger.LogInformation("Saving voice prompt template");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.VoicePrompt = template;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task ResetCharacterPromptAsync()
        {
            _logger.LogInformation("Resetting character prompt to built-in default");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.CharacterPrompt = null;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task ResetBatchCharacterPromptAsync()
        {
            _logger.LogInformation("Resetting batch character prompt to built-in default");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.BatchCharacterPrompt = null;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task<(int Before, int After)> GetContextWindowAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return (
                row?.ContextParagraphsBefore ?? PromptTemplates.DefaultContextParagraphsBefore,
                row?.ContextParagraphsAfter ?? PromptTemplates.DefaultContextParagraphsAfter
            );
        }

        public async Task SetContextWindowAsync(int before, int after)
        {
            _logger.LogInformation("Saving context window: {Before} before, {After} after", before, after);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.ContextParagraphsBefore = before;
            row.ContextParagraphsAfter = after;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task SetVoicePlanPromptAsync(string template)
        {
            _logger.LogInformation("Saving voice plan prompt template");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.VoicePlanPrompt = template;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task ResetVoicePlanPromptAsync()
        {
            _logger.LogInformation("Resetting voice plan prompt to built-in default");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.VoicePlanPrompt = null;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task SetNarratorVoicePlanPromptAsync(string template)
        {
            _logger.LogInformation("Saving narrator voice plan prompt template");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.NarratorVoicePlanPrompt = template;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task ResetNarratorVoicePlanPromptAsync()
        {
            _logger.LogInformation("Resetting narrator voice plan prompt to built-in default");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.NarratorVoicePlanPrompt = null;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        public async Task ResetVoicePromptAsync()
        {
            _logger.LogInformation("Resetting voice prompt to built-in default");
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await EnsureRowAsync(db);
            row.VoicePrompt = null;
            await db.SaveChangesAsync();
            NotifyChanged();
        }

        private static async Task<LlmPromptSettings> EnsureRowAsync(Read2MeDbContext db)
        {
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            if (row == null)
            {
                row = new LlmPromptSettings();
                db.PromptSettings.Add(row);
            }
            return row;
        }
    }
}
