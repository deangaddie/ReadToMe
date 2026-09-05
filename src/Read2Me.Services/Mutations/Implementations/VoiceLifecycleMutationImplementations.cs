using Microsoft.EntityFrameworkCore;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Core.Utils;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the Voice and Voice Rule family shares (ADR 0007). Every mutation here writes a Voice row or
/// a Voice Rule row and nothing else — none of them names a Paragraph, and none of them writes one.
/// <para>
/// They still reach the whole Book, indirectly and by design: a Book View labels each item with the
/// Voice the Audio Queue would resolve for it, and that label is a function of the Character's
/// Voices, its rules and the item's position. So the effects are honestly
/// <see cref="BookMutationScope.Exact"/> about the nothing they name, and the facets —
/// <see cref="BookFacets.Voices"/> and <see cref="BookFacets.VoiceRules"/>, neither of which a reader
/// can place on one Paragraph — are what send a reader back to reresolve the previews it holds.
/// </para>
/// <para>
/// The default Voice Rule is the invariant the family protects: a Character with Voices has exactly
/// one, its floor Rank keeps it below every positional rule, and it is created, repointed and
/// removed here rather than by anyone editing rules directly.
/// </para>
/// </summary>
internal static class VoiceEffects
{
    /// <summary>
    /// Exact about the nothing it names: these mutations write a Voice row or a rule row, and no
    /// Paragraph or item is theirs to enumerate. The facets are what send a reader back.
    /// </summary>
    public static BookMutationEffects Exact(BookFacets facets, Guid? createdId = null) => new()
    {
        Scope = BookMutationScope.Exact,
        Facets = facets,
        CreatedId = createdId,
    };

    public static BookMutationRejectedException NotFound(string what, Guid id) =>
        new(BookMutationRejection.NotFound, $"No {what} {id} in this project.");

    public static BookMutationRejectedException DefaultRule(string verb) =>
        new(BookMutationRejection.Validation,
            $"The default Voice Rule cannot be {verb} — it is the fallback every position lands on. " +
            "Change which Voice it names instead.");

    /// <summary>The Voice, or the rejection every mutation that names one shares.</summary>
    public static async Task<VoiceEntity> VoiceAsync(ProjectDbContext db, Guid voiceId, CancellationToken ct) =>
        await db.Voices.FirstOrDefaultAsync(v => v.Id == voiceId, ct)
            ?? throw NotFound("voice", voiceId);

    /// <summary>
    /// A positional rule, or the rejection its gesture shares. The default rule is refused here
    /// rather than ignored: deleting or reordering it is not something a caller can have meant.
    /// </summary>
    public static async Task<VoiceRule> PositionalRuleAsync(
        ProjectDbContext db, Guid ruleId, string verb, CancellationToken ct)
    {
        var rule = await db.VoiceRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct)
            ?? throw NotFound("voice rule", ruleId);

        return rule.IsDefault ? throw DefaultRule(verb) : rule;
    }

    /// <summary>
    /// The Voice Rule every position falls back to, at the Rank that keeps it below the positional
    /// ones — <c>OrderHelper.GetBefore(null)</c> sorts before every key the appends produce.
    /// </summary>
    public static VoiceRule NewDefaultRule(Guid characterId, Guid voiceId) => new()
    {
        Id = Guid.NewGuid(),
        CharacterId = characterId,
        VoiceId = voiceId,
        IsDefault = true,
        Rank = OrderHelper.GetBefore(null),
    };

}

/// <summary>
/// Adds a Voice to a Character, and the default Voice Rule with it when the Character had none — a
/// Voice nothing can resolve to is not a Voice the Book can be read in.
/// <para>
/// Voices are not deduplicated by name the way Characters are: two takes of the same person are two
/// Voices, and naming them alike is a labelling choice rather than a claim that they are one
/// recording.
/// </para>
/// </summary>
public sealed class CreateVoiceMutationImplementation : IBookMutationImplementation<CreateVoiceMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        CreateVoiceMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == mutation.CharacterId, ct)
            ?? throw VoiceEffects.NotFound("character", mutation.CharacterId);

        // Asked before the new row is staged, so "first" means the Character had none.
        var isFirst = !await db.Voices.AnyAsync(v => v.CharacterId == mutation.CharacterId, ct);

        var voice = new VoiceEntity
        {
            Id = Guid.NewGuid(),
            CharacterId = mutation.CharacterId,
            // An unnamed Voice takes the Character's name: the picker lists Voices, and a blank row
            // in it names nobody.
            Name = string.IsNullOrWhiteSpace(mutation.VoiceName) ? character.Name : mutation.VoiceName.Trim(),
            Description = mutation.Description?.Trim(),
            DesignPrompt = mutation.DesignPrompt,
            Source = mutation.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded,
            CreatedUtc = DateTime.UtcNow,
        };
        db.Voices.Add(voice);

        if (!isFirst)
            return VoiceEffects.Exact(BookFacets.Voices, voice.Id);

        db.VoiceRules.Add(VoiceEffects.NewDefaultRule(mutation.CharacterId, voice.Id));
        return VoiceEffects.Exact(BookFacets.Voices | BookFacets.VoiceRules, voice.Id);
    }
}

/// <summary>
/// Makes a Voice the one its Character falls back to, by repointing the default Voice Rule rather
/// than by adding another — there is exactly one, always. A Character whose Voices somehow outlived
/// their default rule gets one here, which is the only place that repair can happen.
/// </summary>
public sealed class SetVoiceDefaultMutationImplementation : IBookMutationImplementation<SetVoiceDefaultMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceDefaultMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);

        var defaultRule = await db.VoiceRules
            .FirstOrDefaultAsync(r => r.CharacterId == voice.CharacterId && r.IsDefault, ct);

        if (defaultRule is null)
        {
            db.VoiceRules.Add(VoiceEffects.NewDefaultRule(voice.CharacterId, voice.Id));
            return VoiceEffects.Exact(BookFacets.VoiceRules);
        }

        if (defaultRule.VoiceId == mutation.VoiceId)
            return BookMutationEffects.Nothing;

        defaultRule.VoiceId = mutation.VoiceId;
        return VoiceEffects.Exact(BookFacets.VoiceRules);
    }
}

/// <summary>Renames a Voice and rewrites its description, leaving the audio it names alone.</summary>
public sealed class UpdateVoiceMutationImplementation : IBookMutationImplementation<UpdateVoiceMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        UpdateVoiceMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);

        var name = mutation.VoiceName.Trim();
        var description = mutation.Description?.Trim();
        if (voice.Name == name && voice.Description == description)
            return BookMutationEffects.Nothing;

        voice.Name = name;
        voice.Description = description;
        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>Stores the description a designed Voice is synthesised from.</summary>
public sealed class SetVoiceDesignPromptMutationImplementation
    : IBookMutationImplementation<SetVoiceDesignPromptMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceDesignPromptMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);
        if (voice.DesignPrompt == mutation.Prompt) return BookMutationEffects.Nothing;

        voice.DesignPrompt = mutation.Prompt;
        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>Overrides the voice-design server's settings for this Voice, or clears the override.</summary>
public sealed class SetVoiceDesignSettingsOverrideMutationImplementation
    : IBookMutationImplementation<SetVoiceDesignSettingsOverrideMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceDesignSettingsOverrideMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);
        if (voice.VoiceDesignSettingsOverrideJson == mutation.Json) return BookMutationEffects.Nothing;

        voice.VoiceDesignSettingsOverrideJson = mutation.Json;
        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>Overrides the TTS server's settings for this Voice, or clears the override.</summary>
public sealed class SetVoiceTtsSettingsOverrideMutationImplementation
    : IBookMutationImplementation<SetVoiceTtsSettingsOverrideMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceTtsSettingsOverrideMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);
        if (voice.TtsSettingsOverrideJson == mutation.Json) return BookMutationEffects.Nothing;

        voice.TtsSettingsOverrideJson = mutation.Json;
        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>Stores what a Voice's reference audio says — what a cloning TTS is handed with it.</summary>
public sealed class SetVoiceTranscriptMutationImplementation
    : IBookMutationImplementation<SetVoiceTranscriptMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceTranscriptMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);
        if (voice.Transcript == mutation.Transcript) return BookMutationEffects.Nothing;

        voice.Transcript = mutation.Transcript;
        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>
/// Points a Voice at reference audio the producer has already put in place.
/// <para>
/// Never no-change, even when the string does not move: an upload lands at a path derived from the
/// Voice's id and name, so replacing a Voice's audio writes the same string over different bytes.
/// The path is a name, not the artifact — the same reason
/// <see cref="RecordParagraphItemAudioMutationImplementation"/> reports a recording rather than a
/// column.
/// </para>
/// </summary>
public sealed class SetVoiceAudioMutationImplementation : IBookMutationImplementation<SetVoiceAudioMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceAudioMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);
        voice.AudioFileName = mutation.AudioFileName;
        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>
/// Records a synthesised take of a designed Voice: its audio, the sample text that take speaks, and
/// the prompt it was designed from, together. Never no-change, for the reason
/// <see cref="SetVoiceAudioMutationImplementation"/> is not.
/// </summary>
public sealed class SetVoiceGeneratedMutationImplementation
    : IBookMutationImplementation<SetVoiceGeneratedMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceGeneratedMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);

        voice.AudioFileName = mutation.AudioFileName;
        voice.Transcript = mutation.Transcript;
        voice.DesignPrompt = mutation.DesignPrompt;

        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>
/// Switches a Voice between cloned-from-a-recording and designed-from-a-description.
/// <para>
/// Each direction drops what the other kind cannot use: made uploaded, the design prompt goes,
/// because nothing will be synthesised from it again; made generated, the reference goes, because
/// there is nothing left to clone from.
/// </para>
/// <para>
/// The recording <em>file</em> is not deleted here. Removing it inside the transaction would leave a
/// Voice naming audio that is already gone whenever the commit does not follow — a cancelled batch
/// step is enough — so the file, and the stored original that claims an edit on it, go afterwards
/// through <see cref="Audio.IVoiceAudioWriter"/> (ADR 0007).
/// </para>
/// </summary>
public sealed class SetVoiceSourceMutationImplementation
    : IBookMutationImplementation<SetVoiceSourceMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetVoiceSourceMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);

        var target = mutation.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded;
        var discards = mutation.IsGenerated ? voice.AudioFileName is not null : voice.DesignPrompt is not null;
        if (voice.Source == target && !discards)
            return BookMutationEffects.Nothing;

        voice.Source = target;

        if (!mutation.IsGenerated)
        {
            voice.DesignPrompt = null;
            return VoiceEffects.Exact(BookFacets.Voices);
        }

        if (discards) voice.AudioFileName = null;

        return VoiceEffects.Exact(BookFacets.Voices);
    }
}

/// <summary>
/// Removes a Voice and every rule that pointed at it: the positional ones outright, and — when it
/// was the fallback — the default rule, which follows to the Character's oldest remaining Voice or
/// goes too when that was the last one.
/// <para>
/// Rules are cleared before the Voice because <c>VoiceRules.VoiceId</c> is Restrict: a rule still
/// naming this Voice would refuse the delete rather than cascade.
/// </para>
/// <para>
/// Its audio outlives this by a moment, on purpose: the file and the stored original go after the
/// commit, through <see cref="Audio.IVoiceAudioWriter"/>, so a delete that does not commit cannot
/// leave a Voice naming audio that is gone (ADR 0007).
/// </para>
/// </summary>
public sealed class DeleteVoiceMutationImplementation
    : IBookMutationImplementation<DeleteVoiceMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteVoiceMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);

        var rules = await db.VoiceRules
            .Where(r => r.VoiceId == mutation.VoiceId && !r.IsDefault)
            .ExecuteDeleteAsync(ct);

        rules += await RepointDefaultRuleAsync(db, voice.CharacterId, mutation.VoiceId, ct);

        await db.Voices.Where(v => v.Id == mutation.VoiceId).ExecuteDeleteAsync(ct);

        return VoiceEffects.Exact(
            rules > 0 ? BookFacets.Voices | BookFacets.VoiceRules : BookFacets.Voices);
    }

    /// <summary>
    /// Moves the Character's default rule off the Voice being deleted, or removes it when nothing is
    /// left to fall back to. Returns how many rule rows moved, so the receipt names the Voice Rule
    /// facet only when one actually did.
    /// </summary>
    private static async Task<int> RepointDefaultRuleAsync(
        ProjectDbContext db, Guid characterId, Guid voiceId, CancellationToken ct)
    {
        var targetsThisVoice = await db.VoiceRules
            .AnyAsync(r => r.CharacterId == characterId && r.IsDefault && r.VoiceId == voiceId, ct);
        if (!targetsThisVoice) return 0;

        var oldestRemaining = await db.Voices
            .Where(v => v.CharacterId == characterId && v.Id != voiceId)
            .OrderBy(v => v.CreatedUtc)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(ct);

        // Set-based, and before the Voice row goes: a tracked default rule still naming it would
        // break the foreign key on save.
        return oldestRemaining is { } survivor
            ? await db.VoiceRules
                .Where(r => r.CharacterId == characterId && r.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.VoiceId, survivor), ct)
            : await db.VoiceRules
                .Where(r => r.CharacterId == characterId && r.IsDefault)
                .ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Adds a positional Voice Rule below every rule the Character already has, so a new rule never
/// silently outranks one that was there first.
/// <para>
/// A Voice belonging to somebody else is refused rather than ignored. A rule is a claim about which
/// of <em>this</em> Character's Voices reads a stretch of the Book, and pointing it at another
/// Character's recording is a mistake no caller can have meant.
/// </para>
/// </summary>
public sealed class CreateVoiceRuleMutationImplementation : IBookMutationImplementation<CreateVoiceRuleMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        CreateVoiceRuleMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var voice = await VoiceEffects.VoiceAsync(db, mutation.VoiceId, ct);
        if (voice.CharacterId != mutation.CharacterId)
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                $"Voice {mutation.VoiceId} belongs to another character and cannot be ruled for {mutation.CharacterId}.");

        var maxRank = await db.VoiceRules
            .Where(r => r.CharacterId == mutation.CharacterId)
            .Select(r => (string?)r.Rank)
            .MaxAsync(ct);

        var rule = new VoiceRule
        {
            Id = Guid.NewGuid(),
            CharacterId = mutation.CharacterId,
            VoiceId = mutation.VoiceId,
            IsDefault = false,
            Rank = OrderHelper.GetNextOrder(maxRank),
            FromLevel = mutation.FromLevel,
            FromNodeId = mutation.FromNodeId,
            ToLevel = mutation.ToLevel,
            ToNodeId = mutation.ToNodeId,
        };
        db.VoiceRules.Add(rule);

        return VoiceEffects.Exact(BookFacets.VoiceRules, rule.Id);
    }
}

/// <summary>Removes one positional Voice Rule.</summary>
public sealed class DeleteVoiceRuleMutationImplementation : IBookMutationImplementation<DeleteVoiceRuleMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteVoiceRuleMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var rule = await VoiceEffects.PositionalRuleAsync(db, mutation.RuleId, "deleted", ct);

        db.VoiceRules.Remove(rule);
        return VoiceEffects.Exact(BookFacets.VoiceRules);
    }
}

/// <summary>
/// Moves one positional Voice Rule past its neighbour. Rules are evaluated in Rank order, so this is
/// how a reader decides which of two overlapping rules wins.
/// <para>
/// A rule already at the end of the direction asked for changes nothing — the gesture is legal, the
/// button is simply at its limit, and the default rule's floor Rank is the top the first positional
/// rule stops at.
/// </para>
/// </summary>
public sealed class MoveVoiceRuleMutationImplementation : IBookMutationImplementation<MoveVoiceRuleMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MoveVoiceRuleMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var rule = await VoiceEffects.PositionalRuleAsync(db, mutation.RuleId, "reordered", ct);

        var ordered = await db.VoiceRules
            .Where(r => r.CharacterId == rule.CharacterId && !r.IsDefault)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Rank })
            .ToListAsync(ct);

        var index = ordered.FindIndex(r => r.Id == mutation.RuleId);

        if (mutation.Direction == RuleMoveDirection.Up)
        {
            if (index == 0) return BookMutationEffects.Nothing;

            // Between the predecessor and whatever precedes it — never below the default rule, whose
            // floor Rank is not in this list.
            rule.Rank = OrderHelper.GetBetween(
                index - 2 >= 0 ? ordered[index - 2].Rank : null, ordered[index - 1].Rank);
        }
        else
        {
            if (index == ordered.Count - 1) return BookMutationEffects.Nothing;

            rule.Rank = OrderHelper.GetBetween(
                ordered[index + 1].Rank, index + 2 < ordered.Count ? ordered[index + 2].Rank : null);
        }

        return VoiceEffects.Exact(BookFacets.VoiceRules);
    }
}
