using Microsoft.EntityFrameworkCore;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Core.Utils;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services.Commands.Handlers;

public sealed class CreateVoiceHandler(ProjectDbSession session) : ICommandHandler<CreateVoiceCommand>
{
    public async Task<Guid?> HandleAsync(CreateVoiceCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var character = await db.Characters
            .Include(ch => ch.Voices)
            .FirstOrDefaultAsync(ch => ch.Id == c.CharacterId, ct);
        if (character == null) return null;

        var isFirst = !character.Voices.Any();
        var effectiveName = string.IsNullOrWhiteSpace(c.Name) ? character.Name : c.Name.Trim();
        var voice = new VoiceEntity
        {
            Id = Guid.NewGuid(),
            CharacterId = c.CharacterId,
            Name = effectiveName,
            Source = c.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded,
            CreatedUtc = DateTime.UtcNow,
        };
        db.Voices.Add(voice);

        if (isFirst)
        {
            // First voice: create the default VoiceRule (IsDefault=true, null anchors, floor Rank).
            // Floor Rank = OrderHelper.GetBefore(null) = "a0" so it sorts before all non-default rules.
            db.VoiceRules.Add(new VoiceRule
            {
                Id = Guid.NewGuid(),
                CharacterId = c.CharacterId,
                VoiceId = voice.Id,
                IsDefault = true,
                Rank = OrderHelper.GetBefore(null),
            });
        }

        await db.SaveChangesAsync(ct);
        return voice.Id;
    }
}

public sealed class SetVoiceDefaultHandler(ProjectDbSession session) : ICommandHandler<SetVoiceDefaultCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceDefaultCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;

        // Repoint the character's default rule to this voice (one rule, always exactly one).
        var defaultRule = await db.VoiceRules
            .FirstOrDefaultAsync(r => r.CharacterId == voice.CharacterId && r.IsDefault, ct);

        if (defaultRule != null)
        {
            defaultRule.VoiceId = voice.Id;
        }
        else
        {
            // Guard: voices exist but no default rule — create one.
            db.VoiceRules.Add(new VoiceRule
            {
                Id = Guid.NewGuid(),
                CharacterId = voice.CharacterId,
                VoiceId = voice.Id,
                IsDefault = true,
                Rank = OrderHelper.GetBefore(null),
            });
        }

        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class UpdateVoiceHandler(ProjectDbSession session) : ICommandHandler<UpdateVoiceCommand>
{
    public async Task<Guid?> HandleAsync(UpdateVoiceCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.Name = c.Name.Trim();
        voice.Description = c.Description?.Trim();
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceDesignPromptHandler(ProjectDbSession session) : ICommandHandler<SetVoiceDesignPromptCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceDesignPromptCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.DesignPrompt = c.Prompt;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceSettingsOverrideHandler(ProjectDbSession session) : ICommandHandler<SetVoiceSettingsOverrideCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceSettingsOverrideCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.VoiceDesignSettingsOverrideJson = c.Json;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceTtsSettingsOverrideHandler(ProjectDbSession session) : ICommandHandler<SetVoiceTtsSettingsOverrideCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceTtsSettingsOverrideCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.TtsSettingsOverrideJson = c.Json;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceTranscriptHandler(ProjectDbSession session) : ICommandHandler<SetVoiceTranscriptCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceTranscriptCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.Transcript = c.Transcript;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceAudioHandler(ProjectDbSession session) : ICommandHandler<SetVoiceAudioCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceAudioCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.AudioFileName = c.AudioFileName;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceGeneratedHandler(ProjectDbSession session) : ICommandHandler<SetVoiceGeneratedCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceGeneratedCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;

        voice.AudioFileName = c.AudioFileName;
        voice.Transcript = c.Transcript;
        voice.DesignPrompt = c.DesignPrompt;

        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class SetVoiceSourceHandler(
    ProjectDbSession session, IFileSystem fs, IVoiceOriginalStore originals)
    : ICommandHandler<SetVoiceSourceCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceSourceCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;

        voice.Source = c.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded;
        if (!c.IsGenerated)
        {
            voice.DesignPrompt = null;
        }
        else if (voice.AudioFileName != null)
        {
            var projectFolder = fs.GetProjectFolderPath(c.FolderId.Value);
            var audioPath = Path.Combine(projectFolder, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));
            if (fs.FileExists(audioPath))
                fs.DeleteFile(audioPath);
            // The live WAV is going; an original that outlived it would claim an edit on audio that
            // no longer exists.
            originals.Delete(c.FolderId, voice.CharacterId, voice.Id);
            voice.AudioFileName = null;
        }

        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class CreateVoiceRuleHandler(ProjectDbSession session) : ICommandHandler<CreateVoiceRuleCommand>
{
    public async Task<Guid?> HandleAsync(CreateVoiceRuleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);

        // Validate voice belongs to character.
        var voiceBelongs = await db.Voices
            .AnyAsync(v => v.Id == c.VoiceId && v.CharacterId == c.CharacterId, ct);
        if (!voiceBelongs) return null;

        // Append below all existing rules: Rank > current max.
        var maxRank = await db.VoiceRules
            .Where(r => r.CharacterId == c.CharacterId)
            .Select(r => (string?)r.Rank)
            .MaxAsync(ct);

        var newRank = OrderHelper.GetNextOrder(maxRank);

        var rule = new VoiceRule
        {
            Id = Guid.NewGuid(),
            CharacterId = c.CharacterId,
            VoiceId = c.VoiceId,
            IsDefault = false,
            Rank = newRank,
            FromLevel = c.FromLevel,
            FromNodeId = c.FromNodeId,
            ToLevel = c.ToLevel,
            ToNodeId = c.ToNodeId,
        };
        db.VoiceRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule.Id;
    }
}

public sealed class DeleteVoiceRuleHandler(ProjectDbSession session) : ICommandHandler<DeleteVoiceRuleCommand>
{
    public async Task<Guid?> HandleAsync(DeleteVoiceRuleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var rule = await db.VoiceRules.FindAsync([c.RuleId], ct);
        if (rule is null || rule.IsDefault) return null;

        db.VoiceRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class MoveVoiceRuleHandler(ProjectDbSession session) : ICommandHandler<MoveVoiceRuleCommand>
{
    public async Task<Guid?> HandleAsync(MoveVoiceRuleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var rule = await db.VoiceRules.FindAsync([c.RuleId], ct);
        if (rule is null || rule.IsDefault) return null;

        // Load all non-default rules for this character, sorted by Rank.
        var nonDefaultRules = await db.VoiceRules
            .Where(r => r.CharacterId == rule.CharacterId && !r.IsDefault)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Rank })
            .ToListAsync(ct);

        var idx = nonDefaultRules.FindIndex(r => r.Id == c.RuleId);
        if (idx < 0) return null;

        if (c.Direction == RuleMoveDirection.Up)
        {
            if (idx == 0) return null; // already top-most non-default (can't go above default)

            // Swap with predecessor: assign Rank between predecessor's predecessor and predecessor.
            var prev = nonDefaultRules[idx - 1];
            var prevPrev = idx - 2 >= 0 ? nonDefaultRules[idx - 2].Rank : null;
            // Default rule rank is always the floor — must stay below all non-default rules.
            // prevPrev is the rank before prev, or null (we'll get a key below prev).
            rule.Rank = OrderHelper.GetBetween(prevPrev, prev.Rank);
        }
        else // Down
        {
            if (idx == nonDefaultRules.Count - 1) return null; // already bottom-most

            var next = nonDefaultRules[idx + 1];
            var nextNext = idx + 2 < nonDefaultRules.Count ? nonDefaultRules[idx + 2].Rank : null;
            rule.Rank = OrderHelper.GetBetween(next.Rank, nextNext);
        }

        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class DeleteVoiceHandler(
    ProjectDbSession session, IFileSystem fs, IVoiceOriginalStore originals)
    : ICommandHandler<DeleteVoiceCommand>
{
    public async Task<Guid?> HandleAsync(DeleteVoiceCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices
            .FirstOrDefaultAsync(v => v.Id == c.VoiceId, ct);
        if (voice == null) return null;

        var characterId = voice.CharacterId;

        if (voice.AudioFileName != null)
        {
            var projectFolder = fs.GetProjectFolderPath(c.FolderId.Value);
            var audioPath = Path.Combine(projectFolder, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));
            if (fs.FileExists(audioPath))
                fs.DeleteFile(audioPath);
        }

        originals.Delete(c.FolderId, characterId, c.VoiceId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Cascade-delete all non-default rules pointing at this voice.
        await db.VoiceRules
            .Where(r => r.VoiceId == c.VoiceId && !r.IsDefault)
            .ExecuteDeleteAsync(ct);

        // Check whether the default rule targets the voice being deleted.
        var defaultRuleTargetsThis = await db.VoiceRules
            .AnyAsync(r => r.CharacterId == characterId && r.IsDefault && r.VoiceId == c.VoiceId, ct);

        if (defaultRuleTargetsThis)
        {
            // Find the oldest remaining voice (excluding the one being deleted).
            var firstRemaining = await db.Voices
                .Where(v => v.CharacterId == characterId && v.Id != c.VoiceId)
                .OrderBy(v => v.CreatedUtc)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(ct);

            if (firstRemaining.HasValue)
            {
                // Repoint the default rule before deleting the voice (avoids FK constraint violation in tracker).
                await db.VoiceRules
                    .Where(r => r.CharacterId == characterId && r.IsDefault)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.VoiceId, firstRemaining.Value), ct);
            }
            else
            {
                // No remaining voices — delete the default rule first, then the voice.
                await db.VoiceRules
                    .Where(r => r.CharacterId == characterId && r.IsDefault)
                    .ExecuteDeleteAsync(ct);
            }
        }

        await db.Voices
            .Where(v => v.Id == c.VoiceId)
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return null;
    }
}
