using System;
using System.Threading.Tasks;
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
        private void NotifyChanged() => OnChanged?.Invoke();

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

        public async Task<string> GetVoicePromptAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.PromptSettings.SingleOrDefaultAsync();
            return string.IsNullOrWhiteSpace(row?.VoicePrompt)
                ? PromptTemplates.DefaultVoicePrompt
                : row.VoicePrompt;
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
