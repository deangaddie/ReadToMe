using System;
using Read2Me.Core.Models;

namespace Read2Me.App.State.Projection
{
    /// <summary>
    /// A transient Book View gesture: something the reader did to the *view* of a Book rather than
    /// to the Book itself (ADR 0007). Expansion, both selections, the view mode and playback are all
    /// state the Book View renders alongside persisted content, so they cross the same interface and
    /// land in the same atomically published snapshot — never as a second, separately timed update.
    /// <para>
    /// A closed set: only <see cref="BookViewProjection.ApplyAsync"/> interprets it, and every case
    /// below is a gesture the MudBlazor Book View can actually make.
    /// </para>
    /// </summary>
    public abstract record BookViewIntent
    {
        private BookViewIntent() { }

        /// <summary>Open or close one hierarchy node. Closing prunes the branch's descendants.</summary>
        public sealed record SetNodeExpanded(BookNodeLevel Level, Guid NodeId, bool Expanded) : BookViewIntent;

        /// <summary>
        /// Switch between the combined tree and the split views. Both selections are dropped: their
        /// roll-ups mean different things in each mode.
        /// </summary>
        public sealed record SetViewMode(BookViewMode Mode) : BookViewIntent;

        /// <summary>Start playing an item's audio, or stop it if it is the one already playing.</summary>
        public sealed record TogglePlayback(Guid ItemId) : BookViewIntent;

        /// <summary>
        /// Check or uncheck one paragraph in the Folder Selection. The ancestry travels with it: a
        /// selection is rolled up by node, and the paragraph alone cannot say which nodes it is under.
        /// </summary>
        public sealed record SetParagraphSelected(
            Guid ParagraphId, ParagraphSelection Ancestry, bool Selected) : BookViewIntent;

        /// <summary>
        /// Check or uncheck every eligible paragraph under a node. <c>UnattributedOnly</c> narrows a
        /// check to the paragraphs still needing a speaker.
        /// </summary>
        public sealed record SetNodeParagraphsSelected(
            BookNodeLevel Level, Guid NodeId, bool Selected, bool UnattributedOnly = false) : BookViewIntent;

        /// <summary>Arm or disarm bulk assign, which fans one picked speaker across the selection.</summary>
        public sealed record SetBulkAssign(bool Armed) : BookViewIntent;

        /// <summary>Check or uncheck one item in the Audio Item Selection.</summary>
        public sealed record SetAudioItemSelected(AudioItemRef Item, bool Selected) : BookViewIntent;

        /// <summary>
        /// Check or uncheck every eligible audio item under a node. <c>NeedsAudioOnly</c> narrows a
        /// check to the items that have no audio yet.
        /// </summary>
        public sealed record SetNodeAudioItemsSelected(
            BookNodeLevel Level, Guid NodeId, bool Selected, bool NeedsAudioOnly = false) : BookViewIntent;

        /// <summary>
        /// Hand the Folder Selection to the character queue. A queueing gesture rather than a view
        /// one, but it empties the selection, so it crosses this interface for the same reason the
        /// rest do: the snapshot must not still show a selection that is gone.
        /// </summary>
        public sealed record QueueSelectedParagraphs : BookViewIntent;

        /// <summary>Hand the Audio Item Selection to the audio queue, emptying it.</summary>
        public sealed record QueueSelectedAudioItems : BookViewIntent;
    }
}
