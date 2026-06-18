using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Read2Me.Services.Commands.Handlers;

namespace Read2Me.Services
{
    public class BookCommandHandler : IBookCommandHandler
    {
        private readonly Dictionary<Type, Func<BookCommand, CancellationToken, Task<Guid?>>> _handlers;

        // Direct-construction ctor — used by existing tests (new BookCommandHandler(session, fs)).
        public BookCommandHandler(ProjectDbSession session, IFileSystem fs)
            : this(BuildHandlers(session, fs)) { }

        // DI ctor — takes pre-built registry for extensibility.
        internal BookCommandHandler(Dictionary<Type, Func<BookCommand, CancellationToken, Task<Guid?>>> handlers)
        {
            _handlers = handlers;
        }

        public async Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
        {
            if (_handlers.TryGetValue(command.GetType(), out var handler))
                return await handler(command, ct);
            throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");
        }

        private static Dictionary<Type, Func<BookCommand, CancellationToken, Task<Guid?>>> BuildHandlers(
            ProjectDbSession session, IFileSystem fs)
        {
            return new Dictionary<Type, Func<BookCommand, CancellationToken, Task<Guid?>>>
            {
                // Delete
                [typeof(DeleteVolumeCommand)]        = Wrap(new DeleteVolumeHandler(session)),
                [typeof(DeletePartCommand)]          = Wrap(new DeletePartHandler(session)),
                [typeof(DeleteChapterCommand)]       = Wrap(new DeleteChapterHandler(session)),
                [typeof(DeleteParagraphCommand)]     = Wrap(new DeleteParagraphHandler(session)),
                [typeof(DeleteParagraphItemCommand)] = Wrap(new DeleteParagraphItemHandler(session)),

                // Update
                [typeof(UpdateVolumeTitleCommand)]        = Wrap(new UpdateVolumeTitleHandler(session)),
                [typeof(UpdatePartTitleCommand)]          = Wrap(new UpdatePartTitleHandler(session)),
                [typeof(UpdateChapterTitleCommand)]       = Wrap(new UpdateChapterTitleHandler(session)),
                [typeof(UpdateParagraphItemTextCommand)]  = Wrap(new UpdateParagraphItemTextHandler(session)),

                // Split
                [typeof(SplitAtPartCommand)]      = Wrap(new SplitAtPartHandler(session)),
                [typeof(SplitAtChapterCommand)]   = Wrap(new SplitAtChapterHandler(session)),
                [typeof(SplitAtParagraphCommand)] = Wrap(new SplitAtParagraphHandler(session)),
                [typeof(SplitAtItemCommand)]      = Wrap(new SplitAtItemHandler(session)),

                // Merge
                [typeof(MergeVolumeCommand)]        = Wrap(new MergeVolumeHandler(session)),
                [typeof(MergePartCommand)]          = Wrap(new MergePartHandler(session)),
                [typeof(MergeChapterCommand)]       = Wrap(new MergeChapterHandler(session)),
                [typeof(MergeParagraphCommand)]     = Wrap(new MergeParagraphHandler(session)),
                [typeof(MergeParagraphItemCommand)] = Wrap(new MergeParagraphItemHandler(session)),

                // Character
                [typeof(SetItemCharacterCommand)]      = Wrap(new SetItemCharacterHandler(session)),
                [typeof(CreateCharacterCommand)]       = Wrap(new CreateCharacterHandler(session)),
                [typeof(SetParagraphCharacterCommand)] = Wrap(new SetParagraphCharacterHandler(session)),
                [typeof(AddCharacterAliasCommand)]     = Wrap(new AddCharacterAliasHandler(session)),
                [typeof(RemoveCharacterAliasCommand)]  = Wrap(new RemoveCharacterAliasHandler(session)),
                [typeof(MergeCharactersCommand)]       = Wrap(new MergeCharactersHandler(session)),
                [typeof(DeleteCharacterCommand)]       = Wrap(new DeleteCharacterHandler(session)),

                // Voice
                [typeof(CreateVoiceCommand)]              = Wrap(new CreateVoiceHandler(session)),
                [typeof(SetVoiceDefaultCommand)]          = Wrap(new SetVoiceDefaultHandler(session)),
                [typeof(UpdateVoiceCommand)]              = Wrap(new UpdateVoiceHandler(session)),
                [typeof(SetVoiceDesignPromptCommand)]     = Wrap(new SetVoiceDesignPromptHandler(session)),
                [typeof(SetVoiceSettingsOverrideCommand)] = Wrap(new SetVoiceSettingsOverrideHandler(session)),
                [typeof(SetVoiceTranscriptCommand)]       = Wrap(new SetVoiceTranscriptHandler(session)),
                [typeof(SetVoiceAudioCommand)]            = Wrap(new SetVoiceAudioHandler(session)),
                [typeof(SetVoiceGeneratedCommand)]        = Wrap(new SetVoiceGeneratedHandler(session)),
                [typeof(SetVoiceSourceCommand)]           = Wrap(new SetVoiceSourceHandler(session, fs)),
                [typeof(DeleteVoiceCommand)]              = Wrap(new DeleteVoiceHandler(session, fs)),

                // Title
                [typeof(AddBookTitleCommand)]     = Wrap(new AddBookTitleHandler(session)),
                [typeof(AddVolumeTitlesCommand)]  = Wrap(new AddVolumeTitlesHandler(session)),
                [typeof(AddPartTitlesCommand)]    = Wrap(new AddPartTitlesHandler(session)),
                [typeof(AddChapterTitlesCommand)] = Wrap(new AddChapterTitlesHandler(session)),

                // Pause
                [typeof(AddPausesCommand)]              = Wrap(new AddPausesHandler(session)),
                [typeof(InsertPauseParagraphCommand)]   = Wrap(new InsertPauseParagraphHandler(session)),

                // Clear
                [typeof(ClearBookContentCommand)] = Wrap(new ClearBookContentHandler(session)),
            };
        }

        private static Func<BookCommand, CancellationToken, Task<Guid?>> Wrap<TCommand>(
            ICommandHandler<TCommand> handler) where TCommand : BookCommand
            => (cmd, ct) => handler.HandleAsync((TCommand)cmd, ct);

        // Keep for backward compat with tests that reference BookCommandHandler.ApplyMutationAsync directly.
        internal static System.Threading.Tasks.Task ApplyMutationAsync(
            Read2Me.Data.ProjectDbContext db, Read2Me.Services.Books.HierarchyMutation mutation)
            => Commands.BookMutationApplier.ApplyMutationAsync(db, mutation);
    }
}
