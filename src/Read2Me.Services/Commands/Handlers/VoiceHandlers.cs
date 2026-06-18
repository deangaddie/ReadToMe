using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services.Commands.Handlers;

internal sealed class CreateVoiceHandler(ProjectDbSession session) : ICommandHandler<CreateVoiceCommand>
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
            IsDefault = isFirst,
            Source = c.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded,
            CreatedUtc = DateTime.UtcNow,
        };
        db.Voices.Add(voice);
        await db.SaveChangesAsync(ct);
        return voice.Id;
    }
}

internal sealed class SetVoiceDefaultHandler(ProjectDbSession session) : ICommandHandler<SetVoiceDefaultCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceDefaultCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;

        await db.Voices
            .Where(v => v.CharacterId == voice.CharacterId && v.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsDefault, false), ct);

        voice.IsDefault = true;
        db.Voices.Update(voice);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class UpdateVoiceHandler(ProjectDbSession session) : ICommandHandler<UpdateVoiceCommand>
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

internal sealed class SetVoiceDesignPromptHandler(ProjectDbSession session) : ICommandHandler<SetVoiceDesignPromptCommand>
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

internal sealed class SetVoiceSettingsOverrideHandler(ProjectDbSession session) : ICommandHandler<SetVoiceSettingsOverrideCommand>
{
    public async Task<Guid?> HandleAsync(SetVoiceSettingsOverrideCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices.FindAsync(c.VoiceId);
        if (voice == null) return null;
        voice.SettingsOverrideJson = c.Json;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class SetVoiceTranscriptHandler(ProjectDbSession session) : ICommandHandler<SetVoiceTranscriptCommand>
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

internal sealed class SetVoiceAudioHandler(ProjectDbSession session) : ICommandHandler<SetVoiceAudioCommand>
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

internal sealed class SetVoiceGeneratedHandler(ProjectDbSession session) : ICommandHandler<SetVoiceGeneratedCommand>
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

internal sealed class SetVoiceSourceHandler(ProjectDbSession session, IFileSystem fs) : ICommandHandler<SetVoiceSourceCommand>
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
            voice.AudioFileName = null;
        }

        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class DeleteVoiceHandler(ProjectDbSession session, IFileSystem fs) : ICommandHandler<DeleteVoiceCommand>
{
    public async Task<Guid?> HandleAsync(DeleteVoiceCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var voice = await db.Voices
            .Include(v => v.Character)
            .FirstOrDefaultAsync(v => v.Id == c.VoiceId, ct);
        if (voice == null) return null;

        var wasDefault = voice.IsDefault;
        var characterId = voice.CharacterId;

        if (voice.AudioFileName != null)
        {
            var projectFolder = fs.GetProjectFolderPath(c.FolderId.Value);
            var audioPath = Path.Combine(projectFolder, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));
            if (fs.FileExists(audioPath))
                fs.DeleteFile(audioPath);
        }

        db.Voices.Remove(voice);
        await db.SaveChangesAsync(ct);

        if (wasDefault)
        {
            var firstRemaining = await db.Voices
                .Where(v => v.CharacterId == characterId)
                .OrderBy(v => v.CreatedUtc)
                .FirstOrDefaultAsync(ct);
            if (firstRemaining != null)
            {
                firstRemaining.IsDefault = true;
                await db.SaveChangesAsync(ct);
            }
        }

        return null;
    }
}
