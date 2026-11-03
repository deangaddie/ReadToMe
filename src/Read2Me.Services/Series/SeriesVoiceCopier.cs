using Microsoft.Extensions.DependencyInjection;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// The series' part in every Book commit (ADR 0018, #213 "Shared Voices"):
    /// <list type="bullet">
    /// <item>A Voice edit of a series-linked Character (<see cref="SeriesVoiceEdits"/>) is copied,
    /// once it commits, into every other linked book, in the same request.</item>
    /// <item>Deleting a shared Voice is refused while another book's positional rules use it, or
    /// while it is the last Voice and other books are linked.</item>
    /// <item><c>MergeCharacters</c> with a linked merged Character is refused; <c>DeleteCharacter</c>
    /// of a linked one unlinks it, and its Voices go in that book only.</item>
    /// <item>A member book's commit to Characters, the narrator, Voices or Voice Rules sends the
    /// series' <c>series</c> signal.</item>
    /// </list>
    /// The copies are themselves Book commits; while they run, this hook stands aside for them.
    /// </summary>
    public sealed class SeriesVoiceCopier(IServiceProvider services, ICharacterReader characters) : IBookCommitHook
    {
        /// <summary>Set while this flow writes the copies, so their own commits aren't copied on (and don't wait for the registry lock it holds).</summary>
        private static readonly AsyncLocal<bool> Copying = new();

        // Resolved when needed: the registry writes through BookMutations, which resolves this hook.
        private SeriesRegistry Registry => services.GetRequiredService<SeriesRegistry>();

        public async Task<BookCommitPlan> PlanAsync(BookMutation mutation, CancellationToken ct)
        {
            if (Copying.Value || SeriesVoiceEdits.IsSeriesCopy(mutation)) return BookCommitPlan.None;
            var folder = mutation.FolderId;

            switch (mutation)
            {
                case MergeCharactersMutation merge when await Registry.PeersAsync(folder, merge.MergedId, ct) is not null:
                    var name = (await characters.GetCharactersAsync(folder)).FirstOrDefault(c => c.Id == merge.MergedId)?.Name ?? "the merged Character";
                    return BookCommitPlan.Refuse(new BookMutationOutcome.Rejected(BookMutationRejection.Blocked,
                        $"{name} is linked to a series character. Unlink {name} first, or merge the other way."));

                case DeleteCharacterMutation delete when await Registry.PeersAsync(folder, delete.CharacterId, ct) is not null:
                    return BookCommitPlan.After(_ => Registry.ForgetCharacterAsync(folder, delete.CharacterId));
            }

            if (SeriesVoiceEdits.Of(mutation) is { } edit && await PeersOfAsync(mutation, edit, ct) is { Others.Count: > 0 } peers)
            {
                if (edit is SeriesVoiceEdits.Deleted deleted
                    && await DeleteRefusalAsync(mutation, peers, deleted.VoiceId) is { } refusal)
                    return BookCommitPlan.Refuse(refusal);
                return BookCommitPlan.After(change => CopyAsync(mutation, edit, peers, change));
            }

            return BookCommitPlan.After(change =>
                (change.Receipt.Effects.Facets & SeriesVoiceEdits.SignalledFacets) != 0
                    ? Registry.SignalMemberAsync(folder)
                    : Task.CompletedTask);
        }

        private async Task<SeriesPeers?> PeersOfAsync(BookMutation mutation, SeriesVoiceEdits.Edit edit, CancellationToken ct)
        {
            var characterId = edit switch
            {
                SeriesVoiceEdits.Created created => created.CharacterId,
                SeriesVoiceEdits.Changed changed => (await characters.GetVoiceAsync(mutation.FolderId, changed.VoiceId))?.CharacterId,
                SeriesVoiceEdits.Deleted deleted => (await characters.GetVoiceAsync(mutation.FolderId, deleted.VoiceId))?.CharacterId,
                _ => null,
            };
            return characterId is { } id ? await Registry.PeersAsync(mutation.FolderId, id, ct) : null;
        }

        private async Task<BookMutationOutcome.Rejected?> DeleteRefusalAsync(BookMutation mutation, SeriesPeers peers, Guid voiceId)
        {
            if (await characters.GetVoiceAsync(mutation.FolderId, voiceId) is not { } voice) return null;
            var (name, characterId) = (voice.Name, voice.CharacterId);
            if (await Registry.VoiceDeleteBlockAsync(peers, mutation.FolderId, characterId, voiceId) is not { } block) return null;
            return new BookMutationOutcome.Rejected(BookMutationRejection.Blocked, SeriesVoiceDeleteRules.Describe(name, block))
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["reason"] = block.Reason.ToString(),
                    ["books"] = block.Books.Select(b => new { folder = b.Folder, title = b.Title, ruleCount = b.RuleCount }).ToList(),
                },
            };
        }

        private async Task CopyAsync(BookMutation mutation, SeriesVoiceEdits.Edit edit, SeriesPeers peers, CommittedBookChange change)
        {
            // A results-only commit has no step; every Voice edit is a state one, so this is a fallback.
            var label = change.StepLabel ?? ProjectHistory.LabelOf(mutation);
            var action = change.StepAction ?? StepAction.Voice;
            Copying.Value = true;
            try
            {
                switch (edit)
                {
                    case SeriesVoiceEdits.Deleted deleted:
                        await Registry.CopyDeleteAsync(peers, deleted.VoiceId, label, action);
                        break;
                    case SeriesVoiceEdits.Changed changed:
                        await Registry.CopyVoiceAsync(peers, mutation.FolderId, changed.VoiceId, label, action);
                        break;
                    case SeriesVoiceEdits.Created when change.Receipt.Effects.CreatedId is { } created:
                        await Registry.CopyVoiceAsync(peers, mutation.FolderId, created, label, action);
                        break;
                }
            }
            finally
            {
                Copying.Value = false;
            }
        }
    }
}
